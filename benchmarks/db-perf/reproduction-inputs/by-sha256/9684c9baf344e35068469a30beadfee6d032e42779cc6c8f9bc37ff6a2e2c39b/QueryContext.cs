using System.Security.Cryptography;
using System.Text;
using Korda.Data;
using Korda.Database;
using Korda.Database.Internal;
using Korda.Query.Internal;

namespace Korda.Query;

// Owner/metadata orchestration only. Expressions are immediately forwarded to
// B09/B11; this class contains no managed SQL tree or provider SQL rendering.
internal sealed class QueryContext : IDisposable
{
    internal DataSession Session { get; }
    internal DataSource Source => Session.Source;
    internal QueryBinding Binding { get; }
    internal TableIdentity Table { get; }
    internal readonly Dictionary<uint, ParameterDefinition> Parameters = [];
    private readonly Dictionary<object, ExpressionReference[]> _projections = new(ReferenceEqualityComparer.Instance);
    private int _projectionFields, _thread, _depth, _disposed;
    private bool _sealed;
    private object? _selection;
    private readonly CancellationTokenSource _stop = new();
    private DatabaseOwner? _plan;
    internal bool HasOrder;
    internal bool UniqueOrder;
    internal uint? SingleAscendingKey;
    internal bool HasOffset;
    internal QueryContext(DataSession session, TableIdentity table, QueryBinding binding)
    { Session = session; Table = table; Binding = binding; }
    internal T Run<T>(Func<T> action, bool mutable = true)
    {
        using var admission = Enter(mutable);
        try { return action(); }
        catch (BindingFailure failure)
        {
            if (failure.Status == Korda.Interop.Abi.NativeStatus.NotSupported)
                throw new DatabaseException("The native query operation is unavailable for this provider, codec or shape.", DatabaseErrorKind.UnsupportedCapability, correlationId: failure.Correlation);
            throw HostFailure.Binding(Source.Binding, failure);
        }
    }
    internal void Run(Action action, bool mutable = true) => Run(() => { action(); return 0; }, mutable);
    internal Admission Enter(bool mutable = true)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _thread) != thread && Interlocked.CompareExchange(ref _thread, thread, 0) != 0)
            throw new DatabaseException("A query already has an active composition call.", DatabaseErrorKind.ConcurrentUse);
        _depth++;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); Session.CheckOpen();
            if (mutable && _sealed) throw new InvalidOperationException("The query has been sealed or submitted.");
            return new(this);
        }
        catch { Exit(); throw; }
    }
    private void Exit() { if (--_depth == 0) Volatile.Write(ref _thread, 0); }
    internal readonly struct Admission(QueryContext owner) : IDisposable { public void Dispose() => owner.Exit(); }
    internal void Same(QueryContext other)
    { if (!ReferenceEquals(this, other)) throw new ArgumentException("The expression or alias belongs to another query."); }
    internal void CheckColumn<TEntity, T>(Column<TEntity, T> column, TableIdentity table) where TEntity : class
    { ArgumentNullException.ThrowIfNull(column); if (!ReferenceEquals(column.Identity, table)) throw new ArgumentException("The column belongs to another table.", nameof(column)); }
    internal SqlExpression<T> Column<TEntity, T>(Column<TEntity, T> column, TableIdentity table, AliasReference alias = default) where TEntity : class => Run(() =>
    {
        CheckColumn(column, table); var reference = Binding.Column(column.Id, alias);
        return new SqlExpression<T>(this, reference, column.Codec, alias.Value == 0 ? column.Id : null);
    });
    internal SqlExpression<T> Value<T>(T value, ValueCodec<T> codec) => Run(() =>
    {
        ArgumentNullException.ThrowIfNull(codec); var bound = Source.BindCodec(codec.Descriptor);
        return new SqlExpression<T>(this, Binding.Value(OperandEncoding.Value(Source.Binding.SemanticProfile, codec, bound, value)), codec);
    });
    internal ExpressionReference Binary(uint operation, ExpressionReference a, ExpressionReference b) => Run(() => Binding.Binary(operation, a, b));
    internal ExpressionReference Unary(uint operation, ExpressionReference a) => Run(() => Binding.Unary(operation, a));
    internal ulong Clause(uint operation, params ControlField[] fields) => Run(() => Binding.Clause(operation, OperandEncoding.Clause(fields)));
    // Keep the same nested admission and failure translation without capturing a
    // borrowed span in a delegate or extending its lifetime beyond the ABI call.
    private ulong Clause(uint operation, ReadOnlySpan<byte> operands)
    {
        using var admission = Enter();
        try { return Binding.Clause(operation, operands); }
        catch (BindingFailure failure)
        {
            if (failure.Status == Korda.Interop.Abi.NativeStatus.NotSupported)
                throw new DatabaseException("The native query operation is unavailable for this provider, codec or shape.", DatabaseErrorKind.UnsupportedCapability, correlationId: failure.Correlation);
            throw HostFailure.Binding(Source.Binding, failure);
        }
    }
    internal static ControlField U(ushort id, ulong value, ushort type = 2) => OperandEncoding.U64(id, value, type);
    internal static ControlField N(ushort id, uint value) => OperandEncoding.U32(id, value);
    internal static ControlField B(ushort id, byte[] value, ushort type = 5) => OperandEncoding.Bytes(id, value, type);
    internal static ControlField Text(ushort id, string value) => OperandEncoding.Text(id, value);
    internal ExpressionReference Ref(ulong value) => new(Binding, value);
    internal void PublishProjection<T>(Projection<T> projection, ExpressionReference[] expressions)
    {
        if (expressions.Length > Source.Limits.MaxQueryNodes - _projectionFields) throw new DatabaseException("Query projection metadata exceeds its finite node budget.", DatabaseErrorKind.ResourceLimit);
        _projections.Add(projection, expressions); _projectionFields += expressions.Length;
    }
    internal QuerySelection<T> Select<T>(Projection<T> projection, ExpressionReference[]? expressions = null) => Run(() =>
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (expressions is null && !_projections.TryGetValue(projection, out expressions)) throw new ArgumentException("The projection was not built by this query.", nameof(projection));
        Span<byte> scratch = stackalloc byte[OperandEncoding.StackScratchBytes];
        Func<CodecDescriptor, uint> bindCodec = Source.BindCodec;
        if (OperandEncoding.TrySelectionInto(Binding, projection, bindCodec, expressions, scratch, out var length))
            Clause(6, scratch[..length]);
        else
            Clause(6, B(1, OperandEncoding.Tokens(Binding, expressions), 8), B(2, OperandEncoding.Projection(projection, bindCodec, expressions.Select(e => e.In(Binding)).ToArray())));
        _selection = projection; return new QuerySelection<T>(this, projection);
    });
    internal void Selection<T>(Projection<T> projection)
    { if (!ReferenceEquals(_selection, projection)) throw new InvalidOperationException("The borrowed selection is no longer the query's current projection."); }
    internal void MergeParameters(IEnumerable<ParameterDefinition> values)
    {
        foreach (var parameter in values) Parameters.TryAdd(parameter.Id, parameter);
    }
    internal ArenaReference Import<T>(QuerySelection<T> other) => Run(() =>
    {
        var context = other.Owner; if (!ReferenceEquals(Source, context.Source)) throw new ArgumentException("The selection belongs to another source.");
        return context.Run(() =>
        {
            context.Selection(other.Projection);
            var result = Binding.SnapshotAndImport(context.Binding); MergeParameters(context.Parameters.Values); return result;
        });
    });
    internal DatabaseOwner Seal(uint terminal) => Run(() =>
    {
        if (HasOffset && !UniqueOrder) throw new InvalidOperationException("Offset paging lost its unique ordering.");
        var plan = Binding.Seal(terminal); _sealed = true; return plan;
    });
    internal byte[] Values(QueryParameters? supplied) => ParameterDefinition.Encode(Source, Parameters.Values, supplied);
    internal void PublishPlan(DatabaseOwner plan)
    {
        Interlocked.Exchange(ref _plan, plan)?.Dispose();
        if (Volatile.Read(ref _disposed) == 0) return;
        // Dispose may have observed no plan between native Seal and publication.
        // Whichever side removes the reference retires it exactly once.
        if (ReferenceEquals(Interlocked.CompareExchange(ref _plan, null, plan), plan)) plan.Dispose();
        throw new ObjectDisposedException(nameof(QueryContext));
    }
    internal TypedStream<T> Stream<T>(Projection<T> projection, uint terminal, CancellationToken cancellationToken, bool buffered = false)
    {
        return Run(() =>
        {
            Selection(projection);
            if (buffered && (Source.Limits.MaxBufferedRows == 0 || Source.Limits.MaxBufferedBytes == 0))
                throw new DatabaseException("Buffered result demand is disabled.", DatabaseErrorKind.ResourceLimit);
            var bytes = Values(null);
            var id = Source.RegisterProjection(projection); var codecs = projection.Fields.Select(f => Source.BindCodec(f.Descriptor)).ToArray();
            var plan = Seal(terminal); PublishPlan(plan);
            return new TypedStream<T>(async (rows, token) =>
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                try
                {
                    var transport = await Session.OpenPlanStreamAsync(plan, bytes, rows, linked.Token).ConfigureAwait(false);
                    // The linked cancellation owner must outlive the cursor, not only create.
                    return new QueryTransport(transport, linked);
                }
                catch { linked.Dispose(); throw; }
                finally { plan.Dispose(); }
            }, projection, Source.Binding.SemanticProfile, id, codecs, Session.StreamingLimits, cancellationToken);
        });
    }
    internal Task<long> Count(uint terminal, CancellationToken cancellationToken) => Run(() =>
    {
        var bytes = Values(null); var plan = Seal(terminal); PublishPlan(plan);
        return Execute();
        async Task<long> Execute()
        {
            using (plan)
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token))
                return await Session.ExecuteCountAsync(plan, bytes, linked.Token).ConfigureAwait(false);
        }
    });
    internal unsafe string Render<T>(Projection<T> projection) => Run(() =>
    {
        Selection(projection); using var query = Binding.Enter(); nint output = 0; var error = BindingChecks.Error();
        HostFailure.Check(Source.Binding, Binding.Calls.nw_db_query_render_v1(query.Pointer, &output, &error), error);
        using var buffer = new NativeBuffer(Source.Binding, output); return HostEncoding.Utf8.GetString(buffer.Copy());
    });
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop.Cancel(); } finally { Interlocked.Exchange(ref _plan, null)?.Dispose(); Binding.Dispose(); _stop.Dispose(); }
    }
    // Cancellation remains attached until actual B06/B04 retirement. No row owner
    // or native borrow is replaced by this lightweight registration owner.
    private sealed class QueryTransport : IStreamTransport
    {
        private readonly IStreamTransport _inner;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationTokenRegistration _registration;
        internal QueryTransport(IStreamTransport inner, CancellationTokenSource linked)
        { _inner = inner; _linked = linked; _registration = linked.Token.Register(static o => ((IStreamTransport)o!).RequestStop(), inner); }
        public ValueTask<IStreamBatch> FetchAsync() => _inner.FetchAsync();
        public void RequestStop() => _inner.RequestStop();
        public async Task<StreamRetirement> JoinAsync()
        { try { return await _inner.JoinAsync().ConfigureAwait(false); } finally { _registration.Dispose(); _linked.Dispose(); } }
    }
}

internal sealed record ParameterDefinition(DataSource Source, uint Id, string Name, CodecDescriptor Codec, uint BoundCodec)
{
    internal static byte[] Encode(DataSource source, IEnumerable<ParameterDefinition> definitions, QueryParameters? supplied)
    {
        var ordered = definitions.OrderBy(p => p.Id).ToArray();
        var values = supplied?.Snapshot() ?? [];
        if (values.Length != ordered.Length) throw new ArgumentException("Execution requires every declared parameter exactly once and no extra parameters.", nameof(supplied));
        var cells = new ReadOnlyMemory<byte>?[ordered.Length]; var descriptors = new WireDescriptor[ordered.Length];
        var json = new StringBuilder("{\"version\":1,\"parameters\":[");
        for (var i = 0; i < ordered.Length; i++)
        {
            var parameter = ordered[i]; var value = values[i];
            if (!ReferenceEquals(source, parameter.Source) || value.Parameter != parameter) throw new ArgumentException("Parameter source, identity or codec differs from the sealed shape.", nameof(supplied));
            cells[i] = value.Bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(value.Bytes);
            descriptors[i] = new(parameter.Id, parameter.Codec, parameter.BoundCodec);
            if (i != 0) json.Append(',');
            json.Append("{\"id\":").Append(parameter.Id).Append(",\"name\":").Append(Canonical.Quote(parameter.Name)).Append(",\"codec\":").Append(parameter.Codec.Id)
                .Append(",\"codec_config\":").Append(parameter.Codec.Configuration ?? "null").Append(",\"nullable\":").Append(parameter.Codec.Nullable ? "true" : "false").Append('}');
        }
        json.Append("]}");
        return DbWire.Encode(new(PayloadKind.Parameters, source.Binding.SemanticProfile, SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString())), 0, 0, descriptors) { ExactRows = 1 }, 1, cells);
    }
}
internal sealed record ParameterSnapshot(ParameterDefinition Parameter, byte[]? Bytes);

// One immutable layout belongs to one prepared plan. Execution still snapshots
// values and validates their exact source/identity; only shape metadata is reused.
internal sealed class PreparedParameterLayout
{
    private readonly ParameterDefinition[] _definitions;
    private readonly WireExpectation _expectation;
    private readonly byte[]? _parameterlessPayload;

    internal PreparedParameterLayout(DataSource source, IEnumerable<ParameterDefinition> definitions)
    {
        _definitions = definitions.OrderBy(parameter => parameter.Id).ToArray();
        var descriptors = new WireDescriptor[_definitions.Length];
        var json = new StringBuilder("{\"version\":1,\"parameters\":[");
        for (var i = 0; i < _definitions.Length; i++)
        {
            var parameter = _definitions[i];
            if (!ReferenceEquals(source, parameter.Source)) throw new ArgumentException("Parameter source differs from the prepared shape.", nameof(definitions));
            descriptors[i] = new(parameter.Id, parameter.Codec, parameter.BoundCodec);
            if (i != 0) json.Append(',');
            json.Append("{\"id\":").Append(parameter.Id).Append(",\"name\":").Append(Canonical.Quote(parameter.Name)).Append(",\"codec\":").Append(parameter.Codec.Id)
                .Append(",\"codec_config\":").Append(parameter.Codec.Configuration ?? "null").Append(",\"nullable\":").Append(parameter.Codec.Nullable ? "true" : "false").Append('}');
        }
        json.Append("]}");
        _expectation = new(PayloadKind.Parameters, source.Binding.SemanticProfile, SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString())), 0, 0, descriptors) { ExactRows = 1 };
        // The ABI accepts a read-only input view and owns its rendered parameter
        // bytes before returning from execute_create. No caller can mutate this
        // array, so independent lazy executions can safely share the empty frame.
        if (_definitions.Length == 0) _parameterlessPayload = DbWire.Encode(_expectation, 1, []);
    }

    internal byte[] Encode(DataSource source, QueryParameters? supplied)
    {
        var values = supplied?.Snapshot() ?? [];
        if (values.Length != _definitions.Length) throw new ArgumentException("Execution requires every declared parameter exactly once and no extra parameters.", nameof(supplied));
        if (_parameterlessPayload is { } empty) return empty;
        var cells = new ReadOnlyMemory<byte>?[_definitions.Length];
        for (var i = 0; i < _definitions.Length; i++)
        {
            var parameter = _definitions[i]; var value = values[i];
            if (!ReferenceEquals(source, parameter.Source) || value.Parameter != parameter) throw new ArgumentException("Parameter source, identity or codec differs from the sealed shape.", nameof(supplied));
            cells[i] = value.Bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(value.Bytes);
        }
        return DbWire.Encode(_expectation, 1, cells);
    }
}
