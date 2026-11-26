using System.Buffers.Binary;
using System.Text;
using Korda.Data;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

// Records the public Fluent operation vocabulary. This is a transport tape and
// the small validation facts needed at the existing call boundaries; it neither
// renders SQL nor chooses a dialect, rewrites a query, or executes client code.
internal sealed partial class QueryCommand
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly DatabaseOwner _session;
    private readonly RetainedQueryOwner _root;
    private readonly QueryCommandEnvironment _environment;
    private readonly QueryModelMetadata _model;
    private byte[] _records = new byte[256];
    private int _written, _recordCount, _references = 1;
    private readonly List<Node> _slots = [];
    private readonly List<Dependency> _dependencies = [];
    private readonly List<Fixup> _fixups = [];
    private readonly List<Origin> _origins = [];
    private readonly Dictionary<ShapeKey, int> _shapes = [];
    private readonly Dictionary<uint, QueryParameterMetadata> _parameters = [];
    private readonly Dictionary<object, int> _literals = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _groups = [];
    private readonly HashSet<uint> _assigned = [];
    private readonly HashSet<string> _cteNames = new(StringComparer.Ordinal);
    private Expression[]? _selection;
    private CodecDescriptor[]? _projection;
    private int _nextShape, _nodes, _retainedNodes, _ownedBytes, _expanded = 1, _statementNodes = 1, _depth = 1, _importedDepth;
    private uint _reserved;
    private bool _sealed, _hasWhere, _hasJoin, _grouped, _hasPaging, _advancedMutation;

    internal SourceBinding Source => _session.Source;
    private QueryCommandCalls Calls => _environment.Calls;

    internal QueryCommand(DatabaseOwner session, DatabaseOwner model, uint tableId)
    {
        BindingChecks.SameSource(session.Source, model);
        _environment = session.Source.Commands ?? throw new BindingFailure(NativeStatus.NotSupported, 0, 11, 0);
        _environment.Require("db.query.command.v1"); _environment.Require("db.query.v1");
        _environment.Require("db.metadata.v1"); _environment.Require("db.scalar-codecs.v1");
        _environment.Calls.Require();
        _model = model.QueryModel ?? throw new ArgumentException("The registered model has no immutable query metadata.");
        if (_model.TableId != tableId) throw new ArgumentException("Query root table identity differs.");
        using var sessionLease = session.Borrow(DatabaseOwnerKind.Session);
        _session = session; _root = new(model, DatabaseOwnerKind.Model);
    }

    private QueryCommand(QueryCommand source)
    {
        _session = source._session; _root = source._root.Retain(); _environment = source._environment; _model = source._model;
        _records = source._records.AsSpan(0, source._written).ToArray(); _written = source._written; _recordCount = source._recordCount;
        _slots.AddRange(source._slots); _fixups.AddRange(source._fixups);
        foreach (var origin in source._origins) _origins.Add(new(origin.Offset, origin.Identity.Retain()));
        foreach (var dependency in source._dependencies) _dependencies.Add(dependency.Retain());
        foreach (var item in source._shapes) _shapes.Add(item.Key, item.Value);
        foreach (var item in source._parameters) _parameters.Add(item.Key, item.Value);
        foreach (var item in source._literals) _literals.Add(item.Key, item.Value);
        _groups.UnionWith(source._groups); _assigned.UnionWith(source._assigned); _cteNames.UnionWith(source._cteNames);
        _selection = source._selection; _projection = source._projection;
        _nextShape = source._nextShape; _nodes = source._nodes; _retainedNodes = source._retainedNodes;
        _ownedBytes = source._ownedBytes; _expanded = source._expanded; _statementNodes = source._statementNodes;
        _depth = source._depth; _importedDepth = source._importedDepth;
        _hasWhere = source._hasWhere; _hasJoin = source._hasJoin; _grouped = source._grouped;
        _hasPaging = source._hasPaging; _advancedMutation = source._advancedMutation;
    }

    internal QueryCommand Clone()
    {
        Mutable(false);
        return new(this);
    }

    private QueryCommand Retain()
    {
        if (Interlocked.Increment(ref _references) <= 1) throw new ObjectDisposedException(nameof(QueryCommand));
        return this;
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        _root.Release();
        foreach (var dependency in _dependencies) dependency.Release();
        foreach (var origin in _origins) origin.Identity.Release();
        _dependencies.Clear(); _records = []; _slots.Clear(); _fixups.Clear(); _origins.Clear();
    }

    private void Mutable(bool expression)
    {
        if (_sealed) throw new InvalidOperationException("The query has been sealed.");
        if (_reserved != 0 && !expression) throw new InvalidOperationException("Only expressions may be composed during a JOIN callback.");
    }

    internal static void Capacity() => throw new BindingFailure(NativeStatus.DomainError, 0x4442, 11, 0);
    private static void Unsupported() => throw new BindingFailure(NativeStatus.NotSupported, 0, 11, 0);
    private int LiteralBytes
    {
        get
        {
            var bytes = 0;
            foreach (var literal in _literals) bytes = checked(bytes + literal.Value);
            return bytes;
        }
    }

    private void Quota(int bytes, int depth = 0, int expanded = 0, int literalBytes = 0, int retainedNodes = 0)
    {
        if (_nodes + _retainedNodes >= _environment.MaxNodes || retainedNodes >= _environment.MaxNodes - _nodes - _retainedNodes
            || bytes > _environment.MaxBytes - _ownedBytes || depth > _environment.MaxDepth
            || expanded > _environment.MaxNodes - _expanded || literalBytes > _environment.MaxBytes - LiteralBytes)
            Capacity();
    }

    private Node At(ulong token, NodeKind kind)
    {
        if (token == 0 || token > (ulong)_slots.Count) throw new ArgumentException("Default or foreign query-local token.");
        var node = _slots[(int)token - 1];
        if (!node.Valid || node.Kind != kind) throw new ArgumentException("Expired or incompatible query-local token.");
        return node;
    }

    private Expression Expr(ulong token) => At(token, NodeKind.Expression).Expression!;
    private ShapeIdentity Shape(ShapeKey key)
    {
        return _shapes.TryGetValue(key, out var existing) ? new(existing) : new(0, key);
    }
    private static bool Numeric(uint id) => id is >= 1 and <= 3 or >= 11 and <= 13 or 15;
    private static CodecDescriptor Boolean(bool nullable = false) => nullable ? Codecs.Boolean.Descriptor with { Nullable = true } : Codecs.Boolean.Descriptor;
    private static uint[]? Dependencies(params Expression[] values)
    {
        uint[]? found = null;
        foreach (var value in values)
        {
            if (value.Dependencies is null) continue;
            if (found is null) found = value.Dependencies;
            else if (!ReferenceEquals(found, value.Dependencies)) found = found.Union(value.Dependencies).Order().ToArray();
        }
        return found;
    }

    private uint Append(ushort kind, uint operation, ReadOnlySpan<byte> payload, Node? result, int bytes,
        int expanded = 0, int statement = 0, QueryOrigin? origin = null, bool marker = true)
    {
        var length = checked(16 + payload.Length);
        RecordQuota(payload.Length);
        var required = checked(_written + length);
        if (required > _records.Length) Array.Resize(ref _records, Math.Min(_environment.MaxEnvelopeBytes, Math.Max(required, Math.Max(256, _records.Length * 2))));
        var target = _records.AsSpan(_written, length); target[..16].Clear();
        var token = result is null ? 0u : checked((uint)_slots.Count + 1);
        DbWire.U32(target, 0, (uint)length); DbWire.U16(target, 4, kind); DbWire.U16(target, 6, checked((ushort)operation)); DbWire.U32(target, 8, token);
        payload.CopyTo(target[16..]);
        if (origin is not null) _origins.Add(new(_written + 12, origin));
        _written = required; _recordCount++;
        if (result is not null) _slots.Add(result);
        if (marker) _nodes++;
        _ownedBytes += bytes; _expanded += expanded; _statementNodes += statement;
        return token;
    }

    private void RecordQuota(int payloadBytes)
    {
        if (_recordCount >= 2 * _environment.MaxNodes || checked(16 + payloadBytes) > _environment.MaxEnvelopeBytes - _written) Capacity();
    }

    private uint Emit(ushort kind, uint operation, ReadOnlySpan<byte> payload, Expression expression, int bytes, QueryOrigin? origin = null, ShapeIdentity shape = default)
    {
        Quota(bytes, expression.Depth, expression.Nodes); RecordQuota(payload.Length);
        // Failed mutations must not intern unbounded validation metadata. Shape
        // identity becomes persistent only after all finite capacity checks.
        if (expression.Shape == 0)
        {
            var identity = shape.Id;
            if (identity == 0)
            {
                identity = ++_nextShape;
                if (shape.Key is { } key) _shapes.Add(key, identity);
            }
            expression = expression with { Shape = identity };
        }
        var result = Append(kind, operation, payload, new(NodeKind.Expression, Expression: expression), bytes, expression.Nodes, origin: origin);
        _depth = Math.Max(_depth, expression.Depth);
        return result;
    }

    internal ulong Column(uint id, ulong alias)
    {
        Mutable(true);
        var model = _model; var qualifier = model.Table; bool lifted = false;
        if (alias != 0)
        {
            var info = At(alias, NodeKind.Alias).Alias!;
            if (info.Provisional && _reserved != alias) throw new InvalidOperationException("Inactive provisional JOIN alias.");
            model = info.Model; qualifier = info.Name; lifted = info.Left && !info.Provisional;
        }
        var column = model.Column(id); var codec = column.Codec;
        if (lifted && !codec.Nullable) codec = codec with { Nullable = true };
        var value = new Expression(codec, _environment.Bound(codec), 1, 1, false, false,
            alias == 0 ? null : [checked((uint)alias)], 0);
        Span<byte> payload = stackalloc byte[8]; DbWire.U32(payload, 0, checked((uint)alias)); DbWire.U32(payload, 4, id);
        return Emit(1, 0, payload, value, 0, shape: Shape(new(1, 0, Text: column.Name, Qualifier: qualifier)));
    }

    private (CodecDescriptor Codec, uint Bound, int Rows, int LiteralBytes) Values(ReadOnlySpan<byte> bytes, int? rows = null)
    {
        if (bytes.Length < 112 || bytes.Length > _environment.MaxBytes) throw new ArgumentException("Expression wire size.");
        var actualRows = checked((int)DbWire.R32(bytes, 56));
        if (rows is { } exact && actualRows != exact) throw new ArgumentException("Expression wire row count.");
        if (actualRows > _environment.MaxParameters) Capacity();
        var flags = DbWire.R16(bytes, 102); if (flags > 1) throw new ArgumentException("Expression wire flags.");
        var bound = DbWire.R32(bytes, 104); var codec = _environment.Codec(bound, flags == 1);
        var payload = checked(112 + actualRows * 24);
        if (payload > bytes.Length) throw new ArgumentException("Expression wire cell range.");
        return (codec, bound, actualRows, bytes.Length - payload);
    }

    internal ulong Value(ReadOnlySpan<byte> bytes)
    {
        Mutable(true); var input = Values(bytes, 1); Quota(bytes.Length, 1, 1, bytes.Length);
        var origin = _environment.Origin(); var value = new Expression(input.Codec, input.Bound, 1, 1, false, false, null, default);
        var result = Emit(2, 0, bytes, value, bytes.Length, origin); _literals.Add(origin.Identity, input.LiteralBytes); return result;
    }

    internal ulong Parameter(uint id, ReadOnlySpan<byte> name, uint bound, bool nullable)
    {
        Mutable(true);
        if (id == 0 || name.Length is 0 or > 128) throw new ArgumentException("Invalid prepared parameter identity.");
        for (var i = 0; i < name.Length; i++)
            if (name[i] != '_' && name[i] is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z')
                && !(i > 0 && name[i] is >= (byte)'0' and <= (byte)'9')) throw new ArgumentException("Invalid prepared parameter name.");
        var codec = _environment.Codec(bound, nullable); var identity = new QueryParameterMetadata(id, Utf8.GetString(name), codec, bound);
        Quota(name.Length, 1, 1); RecordQuota(16 + name.Length); CheckParameters([identity]); _environment.RegisterParameter(identity);
        Span<byte> payload = stackalloc byte[16 + name.Length]; DbWire.U32(payload, 0, id); DbWire.U32(payload, 4, bound);
        DbWire.U32(payload, 8, nullable ? 1u : 0u); DbWire.U32(payload, 12, (uint)name.Length); name.CopyTo(payload[16..]);
        var result = Emit(3, 0, payload, new(codec, bound, 1, 1, false, false, null, default), name.Length, _environment.Origin());
        _parameters.TryAdd(id, identity); return result;
    }

    internal ulong Binary(uint operation, ulong left, ulong right)
    {
        Mutable(true); var a = Expr(left); var b = Expr(right);
        if (operation is 0 or > 14) Unsupported();
        if (!a.Codec.SameScalar(b.Codec) || a.Bound != b.Bound) throw new ArgumentException("Binary operand codec mismatch.");
        var codec = a.Codec;
        if (operation <= 6) codec = Boolean(a.Codec.Nullable || b.Codec.Nullable);
        else if (operation <= 10 && !Numeric(codec.Id)) Unsupported();
        else if (operation is 11 or 12 && codec.Id != 4) throw new ArgumentException("Boolean operands required.");
        else if (operation == 13) { _environment.Require("db.null-safe-equality.v1"); codec = Boolean(); }
        else codec = codec with { Nullable = operation == 14 ? a.Codec.Nullable && b.Codec.Nullable : a.Codec.Nullable || b.Codec.Nullable };
        Span<byte> payload = stackalloc byte[8]; DbWire.U32(payload, 0, checked((uint)left)); DbWire.U32(payload, 4, checked((uint)right));
        return Emit(4, operation, payload, new(codec, codec.Id == 4 ? 4 : a.Bound, Math.Max(a.Depth, b.Depth) + 1,
            checked(1 + a.Nodes + b.Nodes), a.Aggregate || b.Aggregate, false, Dependencies(a, b), 0), 0, shape: Shape(new(4, operation, a.Shape, b.Shape)));
    }

    internal ulong Unary(uint operation, ulong input)
    {
        Mutable(true); var value = Expr(input); var codec = value.Codec; var bound = value.Bound;
        if (operation is 1 or 2) { codec = Boolean(); bound = 4; }
        else if (operation == 3) { if (codec.Id != 4) throw new ArgumentException("NOT requires a Boolean expression."); }
        else if (operation == 4) { if (codec.Nullable) throw new ArgumentException("NullableLift requires a nonnullable input."); codec = codec with { Nullable = true }; }
        else Unsupported();
        Span<byte> payload = stackalloc byte[4]; DbWire.U32(payload, 0, checked((uint)input));
        return Emit(5, operation, payload, value with
        {
            Codec = codec,
            Bound = bound,
            Depth = value.Depth + 1,
            Nodes = value.Nodes + 1,
            Windowable = operation == 4 && value.Windowable,
            Shape = operation == 4 ? value.Shape : 0
        }, 0,
            shape: operation == 4 ? default : Shape(new(5, operation, value.Shape)));
    }

    internal ulong List(uint operation, ulong input, ReadOnlySpan<byte> bytes)
    {
        Mutable(true); if (operation is 0 or > 3) Unsupported(); var value = Expr(input); var cells = Values(bytes, operation == 3 ? 2 : null);
        if (!value.Codec.SameScalar(cells.Codec) || value.Bound != cells.Bound) throw new ArgumentException("List element codec mismatch.");
        var nodes = checked(value.Nodes + cells.Rows + 1); Quota(bytes.Length, value.Depth + 1, nodes, bytes.Length);
        var payload = new byte[4 + bytes.Length]; DbWire.U32(payload, 0, checked((uint)input)); bytes.CopyTo(payload.AsSpan(4));
        var origin = _environment.Origin();
        var result = Emit(6, operation, payload, new(Boolean(cells.Rows != 0 && (cells.Codec.Nullable || value.Codec.Nullable)), 4,
            value.Depth + 1, nodes, value.Aggregate, false, value.Dependencies, 0), bytes.Length, origin,
            cells.Rows == 0 ? Shape(new(6, operation == 2 ? 2u : 1u)) : default);
        _literals.Add(origin.Identity, cells.LiteralBytes); return result;
    }

    private void CheckParameters(IEnumerable<QueryParameterMetadata> incoming)
    {
        var count = _parameters.Count;
        foreach (var parameter in incoming)
        {
            if (_parameters.TryGetValue(parameter.Id, out var existing))
            {
                if (existing != parameter) throw new ArgumentException("Parameter identity metadata collision.");
                continue;
            }
            if (_parameters.Values.Any(value => value.Name == parameter.Name)) throw new ArgumentException("Different parameters share a composed name.");
            if (++count > _environment.MaxParameters) Capacity();
        }
    }

    internal QueryPlanMetadata Metadata()
    {
        var depth = 1;
        foreach (var node in _slots)
            if (node.Valid && node.Expression is { } expression) depth = Math.Max(depth, expression.Depth);
        return new(_nodes + _retainedNodes, _expanded, _statementNodes, _ownedBytes, Math.Max(_importedDepth, depth),
            new Dictionary<object, int>(_literals, ReferenceEqualityComparer.Instance), _parameters.Values.ToArray(), _projection);
    }

    private void CheckImport(QueryPlanMetadata plan)
    {
        var extraBytes = 0;
        foreach (var literal in plan.Literals) if (!_literals.ContainsKey(literal.Key)) extraBytes = checked(extraBytes + literal.Value);
        Quota(plan.OwnedBytes, plan.Depth + 1, plan.Expanded, extraBytes, plan.Nodes); CheckParameters(plan.Parameters);
    }

    private void Imported(QueryPlanMetadata plan)
    {
        _retainedNodes += plan.Nodes; _importedDepth = Math.Max(_importedDepth, plan.Depth + 1);
        foreach (var parameter in plan.Parameters) _parameters.TryAdd(parameter.Id, parameter);
        foreach (var literal in plan.Literals) _literals.TryAdd(literal.Key, literal.Value);
    }

    internal ulong Import(DatabaseOwner owner, uint kind)
    {
        Mutable(false); BindingChecks.SameSource(Source, owner);
        var expected = kind switch { 1 => DatabaseOwnerKind.Model, 2 => DatabaseOwnerKind.Plan, 3 => DatabaseOwnerKind.Cte, _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        QueryPlanMetadata? plan = kind == 2 ? owner.QueryPlan : kind == 3 ? owner.QueryCte?.Body : null;
        if (kind == 3) _environment.Require("db.cte.v1");
        if (kind == 1 && owner.QueryModel is null || kind != 1 && plan is null) throw new ArgumentException("Missing immutable command dependency metadata.");
        if (plan is null) Quota(0); else CheckImport(plan);
        RecordQuota(4);
        var retained = new RetainedQueryOwner(owner, expected); var index = _dependencies.Count; var fixups = _fixups.Count;
        try
        {
            _dependencies.Add(new(kind, retained, null));
            Span<byte> payload = stackalloc byte[4]; DbWire.U32(payload, 0, (uint)index);
            _fixups.Add(new(_written + 16, index));
            var result = Append(7, kind, payload, new(kind == 1 ? NodeKind.Model : kind == 2 ? NodeKind.Plan : NodeKind.Cte,
                Model: owner.QueryModel, Plan: plan, Cte: owner.QueryCte), plan?.OwnedBytes ?? 0, plan?.Expanded ?? 0);
            if (plan is not null) Imported(plan); return result;
        }
        catch
        {
            if (_dependencies.Count > index) _dependencies.RemoveAt(index);
            if (_fixups.Count > fixups) _fixups.RemoveRange(fixups, _fixups.Count - fixups);
            retained.Release(); throw;
        }
    }

    internal ulong SnapshotAndImport(QueryCommand other)
    {
        if (!ReferenceEquals(Source, other.Source)) throw new ArgumentException("Query sources differ.");
        // The binding captured this immutable snapshot under the source gate.
        // Retain that one copy; copying its complete tape again would duplicate
        // every value and validation table for each subquery composition.
        other.ValidateSeal(8); var snapshot = other.Retain(); var index = _dependencies.Count; var fixups = _fixups.Count;
        try
        {
            Mutable(false); var plan = snapshot.Metadata(); CheckImport(plan); RecordQuota(4);
            _dependencies.Add(new(4, null, snapshot));
            Span<byte> payload = stackalloc byte[4]; DbWire.U32(payload, 0, (uint)index);
            _fixups.Add(new(_written + 16, index));
            var result = Append(7, 4, payload, new(NodeKind.Plan, Plan: plan), plan.OwnedBytes, plan.Expanded);
            Imported(plan); return result;
        }
        catch
        {
            if (_dependencies.Count > index) _dependencies.RemoveAt(index);
            if (_fixups.Count > fixups) _fixups.RemoveRange(fixups, _fixups.Count - fixups);
            snapshot.Release(); throw;
        }
    }

    internal void ValidateSeal(uint terminal)
    {
        Mutable(false);
        if (terminal > 8) Unsupported();
        if ((terminal <= 3 || terminal == 8) && _projection is null) throw new ArgumentException("A selected projection is required.");
        if (terminal is 6 or 7 && (!_hasWhere || _hasJoin || _grouped || _hasPaging || _advancedMutation)) Unsupported();
        if (terminal == 6 && _assigned.Count == 0) throw new ArgumentException("Update assignments are required.");
        if (_grouped && _selection is { } selected)
            foreach (var expression in selected)
                if (!expression.Aggregate && !_groups.Contains(expression.Shape)) throw new ArgumentException("A nonaggregated projection must be a grouping key.");
    }

    private enum NodeKind { Expression, Model, Alias, Window, Plan, Cte }
    private sealed record Expression(CodecDescriptor Codec, uint Bound, int Depth, int Nodes, bool Aggregate, bool Windowable, uint[]? Dependencies, int Shape, bool WindowCast = false);
    private sealed record Alias(QueryModelMetadata Model, string Name, bool Left, bool Provisional);
    private sealed record Node(NodeKind Kind, bool Valid = true, Expression? Expression = null, QueryModelMetadata? Model = null,
        QueryPlanMetadata? Plan = null, QueryCteMetadata? Cte = null, Alias? Alias = null, int WindowNodes = 0);
    private readonly record struct ShapeKey(uint Kind, uint Operation, int A = 0, int B = 0, int C = 0, string? Text = null, string? Qualifier = null);
    private readonly record struct ShapeIdentity(int Id, ShapeKey? Key = null);
    private readonly record struct Fixup(int Offset, int Dependency);
    private readonly record struct Origin(int Offset, QueryOrigin Identity);
    private sealed record Dependency(uint Kind, RetainedQueryOwner? Owner, QueryCommand? Snapshot)
    {
        internal Dependency Retain() => new(Kind, Owner?.Retain(), Snapshot?.Retain());
        internal void Release() { Owner?.Release(); Snapshot?.Release(); }
    }
}
