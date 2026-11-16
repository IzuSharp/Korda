using NativeWeave.Interop.Abi;

namespace NativeWeave.Query.Internal;

/// <summary>A generation-bearing expression reference, never a native pointer or managed SQL node.</summary>
internal readonly record struct ExpressionReference(QueryBinding? Owner, ulong Value)
{
    internal ulong In(QueryBinding owner)
    {
        if (!ReferenceEquals(owner, Owner) || Value == 0) throw new ArgumentException("Default or foreign expression reference.");
        return Value;
    }
}
/// <summary>A query-local alias reference. Only an explicit default means the root table.</summary>
internal readonly record struct AliasReference(QueryBinding? Owner, ulong Value)
{
    internal ulong In(QueryBinding owner)
    {
        if (Owner is null && Value == 0) return 0;
        if (!ReferenceEquals(owner, Owner) || Value == 0) throw new ArgumentException("Foreign alias reference.");
        return Value;
    }
}
/// <summary>A typed alias/model/plan/window dependency token used only by direct native calls.</summary>
internal readonly record struct ArenaReference(QueryBinding? Owner, ulong Value)
{
    internal ulong In(QueryBinding owner)
    {
        if (!ReferenceEquals(owner, Owner) || Value == 0) throw new ArgumentException("Default or foreign arena reference.");
        return Value;
    }
}

/// <summary>Direct native query calls plus one fail-fast admission gate. There is no managed SQL AST.</summary>
/// <remarks>Every byte view is pinned only for the synchronous call. Native copies persistent values before returning.
/// Disposing prevents new admission while SafeHandle leases protect in-flight calls. B13 owns the public Fluent facade.</remarks>
internal sealed unsafe class QueryBinding : IDisposable
{
    private readonly DatabaseOwner _owner;
    private int _active;
    private int _disposed;
    internal QueryBinding(DatabaseOwner owner)
    { if (owner.Kind != DatabaseOwnerKind.Query) throw new ArgumentException("A query owner is required.", nameof(owner)); _owner = owner; }
    internal SourceBinding Source => _owner.Source;
    internal NativeCalls Calls => Source.Calls;
    internal static QueryBinding Create(DatabaseOwner session, DatabaseOwner model, uint tableId)
    {
        BindingChecks.SameSource(session.Source, model);
        var calls = session.Source.Calls; BindingChecks.Require((nint)calls.nw_db_query_create_v1, "query_create");
        using var sessionLease = session.Borrow(DatabaseOwnerKind.Session); using var modelLease = model.Borrow(DatabaseOwnerKind.Model);
        nint result = 0; var error = BindingChecks.Error();
        var status = calls.nw_db_query_create_v1(sessionLease.Pointer, modelLease.Pointer, tableId, &result, &error);
        BindingChecks.Status(status, error);
        return new(new(session.Source, DatabaseOwnerKind.Query, result));
    }
    internal QueryAdmission Enter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("A query already has an active direct call.");
        try { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return new(this, _owner.Borrow(DatabaseOwnerKind.Query)); }
        catch { Volatile.Write(ref _active, 0); throw; }
    }
    internal void Exit() => Volatile.Write(ref _active, 0);
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Dispose(); }
    internal ExpressionReference Column(uint columnId, AliasReference alias = default)
    {
        var aliasValue = alias.In(this); BindingChecks.Require((nint)Calls.nw_db_expr_column_v1, "expr_column");
        using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_expr_column_v1(lease.Pointer, aliasValue, columnId, &result, &error), error); return Expr(result);
    }
    internal ExpressionReference Value(ReadOnlySpan<byte> oneCell)
    {
        BindingChecks.Require((nint)Calls.nw_db_expr_value_v1, "expr_value"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = oneCell) BindingChecks.Status(Calls.nw_db_expr_value_v1(lease.Pointer, BindingChecks.View(bytes, oneCell.Length), &result, &error), error);
        return Expr(result);
    }
    internal ExpressionReference Parameter(uint id, ReadOnlySpan<byte> name, uint boundCodec, bool nullable)
    {
        BindingChecks.Require((nint)Calls.nw_db_expr_parameter_v1, "expr_parameter"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = name) BindingChecks.Status(Calls.nw_db_expr_parameter_v1(lease.Pointer, id, BindingChecks.View(bytes, name.Length), boundCodec, nullable ? 1u : 0u, &result, &error), error);
        return Expr(result);
    }
    internal ExpressionReference Binary(uint operation, ExpressionReference left, ExpressionReference right)
    {
        var a = left.In(this); var b = right.In(this); BindingChecks.Require((nint)Calls.nw_db_expr_binary_v1, "expr_binary");
        using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_expr_binary_v1(lease.Pointer, operation, a, b, &result, &error), error); return Expr(result);
    }
    internal ExpressionReference Unary(uint operation, ExpressionReference input)
    {
        var value = input.In(this); BindingChecks.Require((nint)Calls.nw_db_expr_unary_v1, "expr_unary"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_expr_unary_v1(lease.Pointer, operation, value, &result, &error), error); return Expr(result);
    }
    internal ExpressionReference List(uint operation, ExpressionReference input, ReadOnlySpan<byte> cells)
    {
        var value = input.In(this); BindingChecks.Require((nint)Calls.nw_db_expr_list_v1, "expr_list"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = cells) BindingChecks.Status(Calls.nw_db_expr_list_v1(lease.Pointer, operation, value, BindingChecks.View(bytes, cells.Length), &result, &error), error); return Expr(result);
    }
    internal ArenaReference Import(DatabaseOwner dependency, uint kind)
    {
        BindingChecks.SameSource(Source, dependency);
        var expected = kind switch { 1 => DatabaseOwnerKind.Model, 2 => DatabaseOwnerKind.Plan, 3 => DatabaseOwnerKind.Cte, _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        BindingChecks.Require((nint)Calls.nw_db_query_import_v1, "query_import"); using var lease = Enter(); using var other = dependency.Borrow(expected);
        ulong result = 0; var error = BindingChecks.Error(); BindingChecks.Status(Calls.nw_db_query_import_v1(lease.Pointer, other.Pointer, kind, &result, &error), error);
        if (result == 0) throw new BindingFailure(NativeStatus.ContractViolation, 0, 0, 0); return new(this, result);
    }
    internal ulong Clause(uint operation, ReadOnlySpan<byte> operands)
    {
        BindingChecks.Require((nint)Calls.nw_db_query_clause_v1, "query_clause"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = operands) BindingChecks.Status(Calls.nw_db_query_clause_v1(lease.Pointer, operation, BindingChecks.View(bytes, operands.Length), &result, &error), error);
        return result;
    }
    internal DatabaseOwner Seal(uint terminal)
    {
        BindingChecks.Require((nint)Calls.nw_db_query_seal_v1, "query_seal"); using var lease = Enter(); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_query_seal_v1(lease.Pointer, terminal, &result, &error), error); return new(Source, DatabaseOwnerKind.Plan, result);
    }
    internal QueryBinding CloneSelected()
    {
        BindingChecks.Require((nint)Calls.nw_db_query_clone_v1, "query_clone"); using var lease = Enter(); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_query_clone_v1(lease.Pointer, &result, &error), error); return new(new(Source, DatabaseOwnerKind.Query, result));
    }
    internal ArenaReference SnapshotAndImport(QueryBinding source)
    {
        if (!ReferenceEquals(Source, source.Source)) throw new ArgumentException("Query sources differ.", nameof(source));
        // End source admission before destination admission; self-composition never recursively enters the gate.
        using var clone = source.CloneSelected(); using var plan = clone.Seal(8); return Import(plan, 2);
    }
    private ExpressionReference Expr(ulong value)
    { if (value == 0) throw new BindingFailure(NativeStatus.ContractViolation, 0, 0, 0); return new(this, value); }
}
/// <summary>Combines semantic admission with a synchronous native owner lease.</summary>
internal ref struct QueryAdmission
{
    private QueryBinding? _query; private OwnerLease _lease;
    internal QueryAdmission(QueryBinding query, OwnerLease lease) { _query = query; _lease = lease; }
    internal nint Pointer => _lease.Pointer;
    public void Dispose() { if (_query is not { } query) return; _query = null; _lease.Dispose(); query.Exit(); }
}
