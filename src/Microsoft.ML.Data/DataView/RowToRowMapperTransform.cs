// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.ML.Data;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Internal.Utilities;
using Microsoft.ML.Runtime.Model;
using Microsoft.ML.Runtime.Model.Onnx;
using Microsoft.ML.Runtime.Model.Pfa;

[assembly: LoadableClass(typeof(RowToRowMapperTransform), null, typeof(SignatureLoadDataTransform),
    "", RowToRowMapperTransform.LoaderSignature)]

namespace Microsoft.ML.Runtime.Data
{
    /// <summary>
    /// This interface is used to create a <see cref="RowToRowMapperTransform"/>.
    /// Implementations should be given an <see cref="ISchema"/> in their constructor, and should have a
    /// ctor or Create method with <see cref="SignatureLoadRowMapper"/>, along with a corresponding
    /// <see cref="LoadableClassAttribute"/>.
    /// </summary>
    public interface IRowMapper : ICanSaveModel
    {
        /// <summary>
        /// Returns the input columns needed for the requested output columns.
        /// </summary>
        Func<int, bool> GetDependencies(Func<int, bool> activeOutput);

        /// <summary>
        /// Returns the getters for the output columns given an active set of output columns. The length of the getters
        /// array should be equal to the number of columns added by the IRowMapper. It should contain the getter for the
        /// i'th output column if activeOutput(i) is true, and null otherwise.
        /// </summary>
        Delegate[] CreateGetters(IRow input, Func<int, bool> activeOutput, out Action disposer);

        /// <summary>
        /// Returns information about the output columns, including their name, type and any metadata information.
        /// </summary>
        Schema.DetachedColumn[] GetOutputColumns();
    }

    public delegate void SignatureLoadRowMapper(ModelLoadContext ctx, ISchema schema);

    /// <summary>
    /// This class is a transform that can add any number of output columns, that depend on any number of input columns.
    /// It does so with the help of an <see cref="IRowMapper"/>, that is given a schema in its constructor, and has methods
    /// to get the dependencies on input columns and the getters for the output columns, given an active set of output columns.
    /// </summary>
    public sealed class RowToRowMapperTransform : RowToRowTransformBase, IRowToRowMapper,
        ITransformCanSaveOnnx, ITransformCanSavePfa, ITransformTemplate
    {
        private readonly IRowMapper _mapper;
        private readonly ColumnBindings _bindings;

        // If this is not null, the transform is re-appliable without save/load.
        private readonly Func<Schema, IRowMapper> _mapperFactory;

        public const string RegistrationName = "RowToRowMapperTransform";
        public const string LoaderSignature = "RowToRowMapper";
        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "ROW MPPR",
                verWrittenCur: 0x00010001, // Initial
                verReadableCur: 0x00010001,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature,
                loaderAssemblyName: typeof(RowToRowMapperTransform).Assembly.FullName);
        }

        public override Schema Schema => _bindings.Schema;

        bool ICanSaveOnnx.CanSaveOnnx(OnnxContext ctx) => _mapper is ICanSaveOnnx onnxMapper ? onnxMapper.CanSaveOnnx(ctx) : false;

        bool ICanSavePfa.CanSavePfa => _mapper is ICanSavePfa pfaMapper ? pfaMapper.CanSavePfa : false;

        public RowToRowMapperTransform(IHostEnvironment env, IDataView input, IRowMapper mapper, Func<Schema, IRowMapper> mapperFactory)
            : base(env, RegistrationName, input)
        {
            Contracts.CheckValue(mapper, nameof(mapper));
            Contracts.CheckValueOrNull(mapperFactory);
            _mapper = mapper;
            _mapperFactory = mapperFactory;
            _bindings = new ColumnBindings(Schema.Create(input.Schema), mapper.GetOutputColumns());
        }

        public static Schema GetOutputSchema(ISchema inputSchema, IRowMapper mapper)
        {
            Contracts.CheckValue(inputSchema, nameof(inputSchema));
            Contracts.CheckValue(mapper, nameof(mapper));
            return new ColumnBindings(Schema.Create(inputSchema), mapper.GetOutputColumns()).Schema;
        }

        private RowToRowMapperTransform(IHost host, ModelLoadContext ctx, IDataView input)
            : base(host, input)
        {
            // *** Binary format ***
            // _mapper

            ctx.LoadModel<IRowMapper, SignatureLoadRowMapper>(host, out _mapper, "Mapper", input.Schema);
            _bindings = new ColumnBindings(Schema.Create(input.Schema), _mapper.GetOutputColumns());
        }

        public static RowToRowMapperTransform Create(IHostEnvironment env, ModelLoadContext ctx, IDataView input)
        {
            Contracts.CheckValue(env, nameof(env));
            var h = env.Register(RegistrationName);
            h.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());
            h.CheckValue(input, nameof(input));
            return h.Apply("Loading Model", ch => new RowToRowMapperTransform(h, ctx, input));
        }

        public override void Save(ModelSaveContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // _mapper

            ctx.SaveModel(_mapper, "Mapper");
        }

        /// <summary>
        /// Produces the set of active columns for the data view (as a bool[] of length bindings.ColumnCount),
        /// a predicate for the needed active input columns, and a predicate for the needed active
        /// output columns.
        /// </summary>
        private bool[] GetActive(Func<int, bool> predicate, out Func<int, bool> predicateInput)
        {
            int n = _bindings.Schema.ColumnCount;
            var active = Utils.BuildArray(n, predicate);
            Contracts.Assert(active.Length == n);

            var activeInput = _bindings.GetActiveInput(predicate);
            Contracts.Assert(activeInput.Length == _bindings.InputSchema.ColumnCount);

            // Get a predicate that determines which outputs are active.
            var predicateOut = GetActiveOutputColumns(active);

            // Now map those to active input columns.
            var predicateIn = _mapper.GetDependencies(predicateOut);

            // Combine the two sets of input columns.
            predicateInput =
                col => 0 <= col && col < activeInput.Length && (activeInput[col] || predicateIn(col));

            return active;
        }

        private Func<int, bool> GetActiveOutputColumns(bool[] active)
        {
            Contracts.AssertValue(active);
            Contracts.Assert(active.Length == _bindings.Schema.ColumnCount);

            return
                col =>
                {
                    Contracts.Assert(0 <= col && col < _bindings.AddedColumnIndices.Count);
                    return 0 <= col && col < _bindings.AddedColumnIndices.Count && active[_bindings.AddedColumnIndices[col]];
                };
        }

        protected override bool? ShouldUseParallelCursors(Func<int, bool> predicate)
        {
            Host.AssertValue(predicate, "predicate");
            if (_bindings.AddedColumnIndices.Any(predicate))
                return true;
            return null;
        }

        protected override IRowCursor GetRowCursorCore(Func<int, bool> predicate, IRandom rand = null)
        {
            Func<int, bool> predicateInput;
            var active = GetActive(predicate, out predicateInput);
            return new RowCursor(Host, Source.GetRowCursor(predicateInput, rand), this, active);
        }

        public override IRowCursor[] GetRowCursorSet(out IRowCursorConsolidator consolidator, Func<int, bool> predicate, int n, IRandom rand = null)
        {
            Host.CheckValue(predicate, nameof(predicate));
            Host.CheckValueOrNull(rand);

            Func<int, bool> predicateInput;
            var active = GetActive(predicate, out predicateInput);

            var inputs = Source.GetRowCursorSet(out consolidator, predicateInput, n, rand);
            Host.AssertNonEmpty(inputs);

            if (inputs.Length == 1 && n > 1 && _bindings.AddedColumnIndices.Any(predicate))
                inputs = DataViewUtils.CreateSplitCursors(out consolidator, Host, inputs[0], n);
            Host.AssertNonEmpty(inputs);

            var cursors = new IRowCursor[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
                cursors[i] = new RowCursor(Host, inputs[i], this, active);
            return cursors;
        }

        void ISaveAsOnnx.SaveAsOnnx(OnnxContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            if (_mapper is ISaveAsOnnx onnx)
            {
                Host.Check(onnx.CanSaveOnnx(ctx), "Cannot be saved as ONNX.");
                onnx.SaveAsOnnx(ctx);
            }
        }

        void ISaveAsPfa.SaveAsPfa(BoundPfaContext ctx)
        {
            Host.CheckValue(ctx, nameof(ctx));
            if (_mapper is ISaveAsPfa pfa)
            {
                Host.Check(pfa.CanSavePfa, "Cannot be saved as PFA.");
                pfa.SaveAsPfa(ctx);
            }
        }

        public Func<int, bool> GetDependencies(Func<int, bool> predicate)
        {
            Func<int, bool> predicateInput;
            GetActive(predicate, out predicateInput);
            return predicateInput;
        }

        Schema IRowToRowMapper.InputSchema => Source.Schema;

        public IRow GetRow(IRow input, Func<int, bool> active, out Action disposer)
        {
            Host.CheckValue(input, nameof(input));
            Host.CheckValue(active, nameof(active));
            Host.Check(input.Schema == Source.Schema, "Schema of input row must be the same as the schema the mapper is bound to");

            disposer = null;
            using (var ch = Host.Start("GetEntireRow"))
            {
                Action disp;
                var activeArr = new bool[Schema.ColumnCount];
                for (int i = 0; i < Schema.ColumnCount; i++)
                    activeArr[i] = active(i);
                var pred = GetActiveOutputColumns(activeArr);
                var getters = _mapper.CreateGetters(input, pred, out disp);
                disposer += disp;
                return new Row(input, this, Schema, getters);
            }
        }

        public IDataTransform ApplyToData(IHostEnvironment env, IDataView newSource)
        {
            Contracts.CheckValue(env, nameof(env));

            Contracts.CheckValue(newSource, nameof(newSource));
            if (_mapperFactory != null)
            {
                var newMapper = _mapperFactory(newSource.Schema);
                return new RowToRowMapperTransform(env.Register(nameof(RowToRowMapperTransform)), newSource, newMapper, _mapperFactory);
            }
            // Revert to serialization. This was how it worked in all the cases, now it's only when we can't re-create the mapper.
            using (var stream = new MemoryStream())
            {
                using (var rep = RepositoryWriter.CreateNew(stream, env))
                {
                    ModelSaveContext.SaveModel(rep, this, "model");
                    rep.Commit();
                }

                stream.Position = 0;
                using (var rep = RepositoryReader.Open(stream, env))
                {
                    IDataTransform newData;
                    ModelLoadContext.LoadModel<IDataTransform, SignatureLoadDataTransform>(env,
                        out newData, rep, "model", newSource);
                    return newData;
                }
            }
        }

        private sealed class Row : IRow
        {
            private readonly IRow _input;
            private readonly Delegate[] _getters;

            private readonly RowToRowMapperTransform _parent;

            public long Batch { get { return _input.Batch; } }

            public long Position { get { return _input.Position; } }

            public Schema Schema { get; }

            public Row(IRow input, RowToRowMapperTransform parent, Schema schema, Delegate[] getters)
            {
                _input = input;
                _parent = parent;
                Schema = schema;
                _getters = getters;
            }

            public ValueGetter<TValue> GetGetter<TValue>(int col)
            {
                bool isSrc;
                int index = _parent._bindings.MapColumnIndex(out isSrc, col);
                if (isSrc)
                    return _input.GetGetter<TValue>(index);

                Contracts.Assert(_getters[index] != null);
                var fn = _getters[index] as ValueGetter<TValue>;
                if (fn == null)
                    throw Contracts.Except("Invalid TValue in GetGetter: '{0}'", typeof(TValue));
                return fn;
            }

            public ValueGetter<UInt128> GetIdGetter() => _input.GetIdGetter();

            public bool IsColumnActive(int col)
            {
                bool isSrc;
                int index = _parent._bindings.MapColumnIndex(out isSrc, col);
                if (isSrc)
                    return _input.IsColumnActive((index));
                return _getters[index] != null;
            }
        }

        private sealed class RowCursor : SynchronizedCursorBase<IRowCursor>, IRowCursor
        {
            private readonly Delegate[] _getters;
            private readonly bool[] _active;
            private readonly ColumnBindings _bindings;
            private readonly Action _disposer;

            public Schema Schema => _bindings.Schema;

            public RowCursor(IChannelProvider provider, IRowCursor input, RowToRowMapperTransform parent, bool[] active)
                : base(provider, input)
            {
                var pred = parent.GetActiveOutputColumns(active);
                _getters = parent._mapper.CreateGetters(input, pred, out _disposer);
                _active = active;
                _bindings = parent._bindings;
            }

            public bool IsColumnActive(int col)
            {
                Ch.Check(0 <= col && col < _bindings.Schema.ColumnCount);
                return _active[col];
            }

            public ValueGetter<TValue> GetGetter<TValue>(int col)
            {
                Ch.Check(IsColumnActive(col));

                bool isSrc;
                int index = _bindings.MapColumnIndex(out isSrc, col);
                if (isSrc)
                    return Input.GetGetter<TValue>(index);

                Ch.AssertValue(_getters);
                var getter = _getters[index];
                Ch.Assert(getter != null);
                var fn = getter as ValueGetter<TValue>;
                if (fn == null)
                    throw Ch.Except("Invalid TValue in GetGetter: '{0}'", typeof(TValue));
                return fn;
            }

            public override void Dispose()
            {
                _disposer?.Invoke();
                base.Dispose();
            }
        }
    }
}
