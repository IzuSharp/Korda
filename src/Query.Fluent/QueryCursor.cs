using Korda.Data;
using Korda.Query.Internal;

namespace Korda.Query;

/// <summary>An immutable typed snapshot of all values in one root-table cursor ordering.</summary>
/// <typeparam name="TEntity">The statically mapped root entity class.</typeparam>
/// <remarks>Values are copied when added. The cursor owns no native resource and can be reused with
/// the same table metadata. Applying it requires an exact non-null column ordering containing the
/// complete primary key; the query supplies each column's ascending or descending direction.</remarks>
public sealed class QueryCursor<TEntity> where TEntity : class
{
    private QueryCursor(TableIdentity table, ColumnValue[] values) { Table = table; Values = values; }
    internal TableIdentity Table { get; }
    internal ColumnValue[] Values { get; }

    /// <summary>Copies the first non-null ordering value from a built table.</summary>
    /// <typeparam name="T">The exact column value type.</typeparam>
    /// <param name="column">The first root-table ordering column.</param>
    /// <param name="value">The non-null value to snapshot.</param>
    /// <returns>A new immutable cursor prefix.</returns>
    /// <exception cref="ArgumentNullException">The column or a required value is null.</exception>
    /// <exception cref="ArgumentException">The column is unbuilt, nullable or the value is invalid.</exception>
    public static QueryCursor<TEntity> Create<T>(Column<TEntity, T> column, T value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!column.Identity.Built || column.Codec.Descriptor.Nullable)
            throw new ArgumentException("A cursor requires a non-null column from a built table.", nameof(column));
        return new(column.Identity, [new(column.Binding, column.Snapshot(value))]);
    }

    /// <summary>Copies the next ordering value and returns a new cursor without changing this instance.</summary>
    /// <typeparam name="T">The exact next column value type.</typeparam>
    /// <param name="column">The next distinct non-null column from the same table.</param>
    /// <param name="value">The non-null value to snapshot.</param>
    /// <returns>A new immutable cursor with the next component appended.</returns>
    /// <exception cref="ArgumentNullException">The column or a required value is null.</exception>
    /// <exception cref="ArgumentException">The column is foreign, repeated, nullable or the value is invalid.</exception>
    public QueryCursor<TEntity> Then<T>(Column<TEntity, T> column, T value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!ReferenceEquals(Table, column.Identity) || column.Codec.Descriptor.Nullable
            || Values.Any(part => part.Column.Definition.Id == column.Id))
            throw new ArgumentException("Cursor components require distinct non-null columns from one table.", nameof(column));
        return new(Table, [.. Values, new(column.Binding, column.Snapshot(value))]);
    }
}

public sealed partial class Query<TEntity> where TEntity : class
{
    private readonly List<(uint? Column, SortDirection Direction)> _cursorOrder = [];
    private bool _compositeSeek;

    /// <summary>Appends a strict lexicographic cursor predicate using the exact current root-column ordering.</summary>
    /// <param name="cursor">Every non-null ordering column and its snapshotted value, including the complete primary key.</param>
    /// <returns>This mutable query with a native cursor predicate and unchanged row limit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> is null.</exception>
    /// <exception cref="ArgumentException">The cursor differs from the exact ordering, omits key columns or conflicts with an offset.</exception>
    /// <exception cref="InvalidOperationException">A composite cursor was already applied or the query is sealed.</exception>
    /// <exception cref="ObjectDisposedException">The query or session has closed.</exception>
    /// <exception cref="DatabaseException">The native query capability or finite capacity rejects composition.</exception>
    /// <remarks>Mixed ascending and descending columns are supported. SQL comparisons, parameters and dialect
    /// rendering remain native. Later ordering changes and offsets reject. A unique root key does not remove
    /// duplicate rows introduced by a one-to-many join or provide a cross-request database snapshot.</remarks>
    public Query<TEntity> SeekAfter(QueryCursor<TEntity> cursor) => Context.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (_compositeSeek) throw new InvalidOperationException("A composite cursor was already applied.");
        if (!ReferenceEquals(cursor.Table, _table.Identity) || Context.HasOffset || !Context.UniqueOrder
            || cursor.Values.Length != _cursorOrder.Count
            || cursor.Values.Where((part, index) => part.Column.Definition.Id != _cursorOrder[index].Column).Any())
            throw new ArgumentException("The cursor must match every non-null root ordering column, include the complete primary key and have no offset.", nameof(cursor));

        ExpressionReference? tail = null;
        for (var i = cursor.Values.Length - 1; i >= 0; i--)
        {
            var part = cursor.Values[i]; var codec = part.Column.Definition.Codec;
            var column = Context.Binding.Column(part.Column.Definition.Id);
            var bytes = DbWire.Encode(new(PayloadKind.ExpressionValues, Context.Source.Binding.SemanticProfile, new byte[32], 0, 0,
                [new(1, codec, Context.Source.BindCodec(codec))])
            { ExactRows = 1 }, 1, [new ReadOnlyMemory<byte>(part.Bytes!)]);
            var value = Context.Binding.Value(bytes);
            var comparison = Context.Binding.Binary(_cursorOrder[i].Direction == SortDirection.Ascending ? 3u : 5u, column, value);
            if (tail is { } next)
            {
                var equal = Context.Binding.Binary(1, column, value);
                comparison = Context.Binding.Binary(12, comparison, Context.Binding.Binary(11, equal, next));
            }
            tail = comparison;
        }
        Context.Clause(1, QueryContext.U(1, tail!.Value.In(Context.Binding)));
        _compositeSeek = true;
        return this;
    });
}
