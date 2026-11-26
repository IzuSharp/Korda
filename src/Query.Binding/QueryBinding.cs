using Korda.Interop.Abi;

namespace Korda.Query.Internal;

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
/// <summary>A typed query-local alias/model/plan/window dependency token.</summary>
internal readonly record struct ArenaReference(QueryBinding? Owner, ulong Value)
{
    internal ulong In(QueryBinding owner)
    {
        if (!ReferenceEquals(owner, Owner) || Value == 0) throw new ArgumentException("Default or foreign arena reference.");
        return Value;
    }
}

/// <summary>A fail-fast query admission gate over either the frozen direct ABI or a complete command.</summary>
/// <remarks>The public Fluent facade explicitly selects the command extension. The direct constructor remains
/// for existing internal ABI consumers; missing command support never selects that path implicitly.</remarks>
internal sealed unsafe class QueryBinding : IDisposable
{
    private readonly DatabaseOwner? _owner;
    private readonly SourceBinding _source;
    private QueryCommand? _command;
    private int _active;
    private int _disposed;
    internal QueryBinding(DatabaseOwner owner)
    { if (owner.Kind != DatabaseOwnerKind.Query) throw new ArgumentException("A query owner is required.", nameof(owner)); _owner = owner; _source = owner.Source; }
    private QueryBinding(QueryCommand command) { _source = command.Source; _command = command; }
    internal SourceBinding Source => _source;
    internal NativeCalls Calls => Source.Calls;
    internal static QueryBinding CreateBuffered(DatabaseOwner session, DatabaseOwner model, uint tableId) => new(new QueryCommand(session, model, tableId));
    internal static QueryBinding Create(DatabaseOwner session, DatabaseOwner model, uint tableId)
    {
        BindingChecks.SameSource(session.Source, model);
        var calls = session.Source.Calls; BindingChecks.Require((nint)calls.nw_db_query_create_v1, "query_create");
        using var sessionLease = session.Borrow(DatabaseOwnerKind.Session); using var modelLease = model.Borrow(DatabaseOwnerKind.Model);
        nint result = 0; var error = BindingChecks.Error();
        var status = calls.nw_db_query_create_v1(sessionLease.Pointer, modelLease.Pointer, tableId, &result, &error);
        BindingChecks.Status(status, error);
        return new(new DatabaseOwner(session.Source, DatabaseOwnerKind.Query, result));
    }
    internal QueryAdmission Enter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("A query already has an active direct call.");
        try { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return new(this, _owner is null ? default : _owner.Borrow(DatabaseOwnerKind.Query)); }
        catch { Exit(); throw; }
    }
    internal void Exit()
    {
        Volatile.Write(ref _active, 0);
        // Another admitted caller may have acquired the gate after the write.
        // Once disposed is visible, a later entrant fails its second check.
        if (Volatile.Read(ref _disposed) != 0 && Volatile.Read(ref _active) == 0) Interlocked.Exchange(ref _command, null)?.Release();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner?.Dispose(); if (Volatile.Read(ref _active) == 0) Interlocked.Exchange(ref _command, null)?.Release();
    }
    internal ExpressionReference Column(uint columnId, AliasReference alias = default)
    {
        var aliasValue = alias.In(this);
        if (_command is { } command) { using var admission = Enter(); return Expr(command.Column(columnId, aliasValue)); }
        BindingChecks.Require((nint)Calls.nw_db_expr_column_v1, "expr_column");
        using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_expr_column_v1(lease.Pointer, aliasValue, columnId, &result, &error), error); return Expr(result);
    }
    internal ExpressionReference Value(ReadOnlySpan<byte> oneCell)
    {
        if (_command is { } command) { using var admission = Enter(); return Expr(command.Value(oneCell)); }
        BindingChecks.Require((nint)Calls.nw_db_expr_value_v1, "expr_value"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = oneCell) BindingChecks.Status(Calls.nw_db_expr_value_v1(lease.Pointer, BindingChecks.View(bytes, oneCell.Length), &result, &error), error);
        return Expr(result);
    }
    internal ExpressionReference Parameter(uint id, ReadOnlySpan<byte> name, uint boundCodec, bool nullable)
    {
        if (_command is { } command) { using var admission = Enter(); return Expr(command.Parameter(id, name, boundCodec, nullable)); }
        BindingChecks.Require((nint)Calls.nw_db_expr_parameter_v1, "expr_parameter"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = name) BindingChecks.Status(Calls.nw_db_expr_parameter_v1(lease.Pointer, id, BindingChecks.View(bytes, name.Length), boundCodec, nullable ? 1u : 0u, &result, &error), error);
        return Expr(result);
    }
    internal ExpressionReference Binary(uint operation, ExpressionReference left, ExpressionReference right)
    {
        var a = left.In(this); var b = right.In(this);
        if (_command is { } command) { using var admission = Enter(); return Expr(command.Binary(operation, a, b)); }
        BindingChecks.Require((nint)Calls.nw_db_expr_binary_v1, "expr_binary");
        using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_expr_binary_v1(lease.Pointer, operation, a, b, &result, &error), error); return Expr(result);
    }
    internal ExpressionReference Unary(uint operation, ExpressionReference input)
    {
        var value = input.In(this);
        if (_command is { } command) { using var admission = Enter(); return Expr(command.Unary(operation, value)); }
        BindingChecks.Require((nint)Calls.nw_db_expr_unary_v1, "expr_unary"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_expr_unary_v1(lease.Pointer, operation, value, &result, &error), error); return Expr(result);
    }
    internal ExpressionReference List(uint operation, ExpressionReference input, ReadOnlySpan<byte> cells)
    {
        var value = input.In(this);
        if (_command is { } command) { using var admission = Enter(); return Expr(command.List(operation, value, cells)); }
        BindingChecks.Require((nint)Calls.nw_db_expr_list_v1, "expr_list"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = cells) BindingChecks.Status(Calls.nw_db_expr_list_v1(lease.Pointer, operation, value, BindingChecks.View(bytes, cells.Length), &result, &error), error); return Expr(result);
    }
    internal ArenaReference Import(DatabaseOwner dependency, uint kind)
    {
        if (_command is { } command) { using var admission = Enter(); return new(this, command.Import(dependency, kind)); }
        BindingChecks.SameSource(Source, dependency);
        var expected = kind switch { 1 => DatabaseOwnerKind.Model, 2 => DatabaseOwnerKind.Plan, 3 => DatabaseOwnerKind.Cte, _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        BindingChecks.Require((nint)Calls.nw_db_query_import_v1, "query_import"); using var lease = Enter(); using var other = dependency.Borrow(expected);
        ulong result = 0; var error = BindingChecks.Error(); BindingChecks.Status(Calls.nw_db_query_import_v1(lease.Pointer, other.Pointer, kind, &result, &error), error);
        if (result == 0) throw new BindingFailure(NativeStatus.ContractViolation, 0, 0, 0); return new(this, result);
    }
    internal ulong Clause(uint operation, ReadOnlySpan<byte> operands)
    {
        if (_command is { } command) { using var admission = Enter(); return command.Clause(operation, operands); }
        BindingChecks.Require((nint)Calls.nw_db_query_clause_v1, "query_clause"); using var lease = Enter(); ulong result = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = operands) BindingChecks.Status(Calls.nw_db_query_clause_v1(lease.Pointer, operation, BindingChecks.View(bytes, operands.Length), &result, &error), error);
        return result;
    }
    internal DatabaseOwner Seal(uint terminal)
    {
        if (_command is { } command) { using var admission = Enter(); return command.Seal(terminal); }
        BindingChecks.Require((nint)Calls.nw_db_query_seal_v1, "query_seal"); using var lease = Enter(); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_query_seal_v1(lease.Pointer, terminal, &result, &error), error); return new(Source, DatabaseOwnerKind.Plan, result);
    }
    internal QueryBinding CloneSelected()
    {
        if (_command is { } command) { using var admission = Enter(); return new(command.Clone()); }
        BindingChecks.Require((nint)Calls.nw_db_query_clone_v1, "query_clone"); using var lease = Enter(); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_query_clone_v1(lease.Pointer, &result, &error), error); return new(new DatabaseOwner(Source, DatabaseOwnerKind.Query, result));
    }
    internal ArenaReference SnapshotAndImport(QueryBinding source)
    {
        if (!ReferenceEquals(Source, source.Source)) throw new ArgumentException("Query sources differ.", nameof(source));
        if (_command is { } command)
        {
            QueryCommand snapshot;
            using (source.Enter()) snapshot = (source._command ?? throw new NotSupportedException("A direct query cannot be silently imported into command composition.")).Clone();
            try { using var admission = Enter(); return new(this, command.SnapshotAndImport(snapshot)); }
            finally { snapshot.Release(); }
        }
        // End source admission before destination admission; self-composition never recursively enters the gate.
        using var clone = source.CloneSelected(); using var plan = clone.Seal(8); return Import(plan, 2);
    }
    internal nint Render()
    {
        if (_command is { } command) { using var admission = Enter(); return command.Render(); }
        BindingChecks.Require((nint)Calls.nw_db_query_render_v1, "query_render"); using var lease = Enter(); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_query_render_v1(lease.Pointer, &result, &error), error); return result;
    }
    internal DatabaseOwner CreateCte(DatabaseOwner model, uint tableId, bool recursive)
    {
        if (_command is { } command) { using var admission = Enter(); return command.CreateCte(model, tableId, recursive); }
        BindingChecks.SameSource(Source, model); BindingChecks.Require((nint)Calls.nw_db_cte_create_v1, "cte_create");
        using var declared = model.Borrow(DatabaseOwnerKind.Model); using var query = Enter(); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_cte_create_v1(declared.Pointer, tableId, query.Pointer, recursive ? 1u : 0u, &result, &error), error);
        return new(Source, DatabaseOwnerKind.Cte, result);
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
