using NativeWeave.Data;

namespace NativeWeave.Database;

/// <summary>An immutable explicit unique-column or PostgreSQL constraint target for one atomic upsert.</summary>
/// <typeparam name="TEntity">The statically described entity class.</typeparam>
/// <remarks>Construction performs no schema query. The native database checks that the selected unique constraint exists.</remarks>
public sealed class ConflictTarget<TEntity> where TEntity : class
{
    private readonly string? _constraint;
    private readonly IColumn[] _columns;
    /// <summary>Creates an explicit named PostgreSQL constraint target.</summary>
    /// <param name="constraintName">A nonempty exact store constraint name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="constraintName"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, invalid or exceeds its finite UTF8 capacity.</exception>
    public ConflictTarget(string constraintName)
    {
        _constraint = Validation.Name(constraintName, nameof(constraintName));
        if (HostEncoding.Utf8.GetByteCount(constraintName) > 256 || constraintName.Any(char.IsControl)) throw new ArgumentException("The constraint name is invalid or too long.", nameof(constraintName));
        _columns = [];
    }
    private ConflictTarget(IColumn[] columns) => _columns = columns;
    /// <summary>Starts an immutable portable conflict target at one built table column.</summary>
    /// <typeparam name="T">The column's exact value type.</typeparam>
    /// <param name="column">A column from the target table.</param>
    /// <returns>A new one-column target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">The table has not been built.</exception>
    public static ConflictTarget<TEntity> Columns<T>(Column<TEntity, T> column)
    { ArgumentNullException.ThrowIfNull(column); if (!column.Identity.Built) throw new ArgumentException("The table must be built.", nameof(column)); return new([column.Binding]); }
    /// <summary>Returns a new target with an additional unique column in the stated order.</summary>
    /// <typeparam name="T">The column's exact value type.</typeparam>
    /// <param name="column">A new column of the same built table.</param>
    /// <returns>A new immutable target; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException">The target is named, or the column is foreign, repeated or unbuilt.</exception>
    public ConflictTarget<TEntity> And<T>(Column<TEntity, T> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (_constraint is not null || !column.Identity.Built || !ReferenceEquals(_columns[0].Identity, column.Identity) || _columns.Any(c => c.Definition.Id == column.Id))
            throw new ArgumentException("Expected a distinct column of this target's built table.", nameof(column));
        return new([.. _columns, column.Binding]);
    }
    internal ControlField Encode(Table<TEntity> table, DataSource source)
    {
        if (_constraint is not null)
        {
            if (source.Provider != DatabaseProvider.PostgreSql) throw new DatabaseCapabilityException("db.upsert.named-constraint", source.Provider.ToString());
            return new(6, 4, HostEncoding.Utf8.GetBytes(_constraint));
        }
        if (_columns.Length == 0 || _columns.Any(c => !ReferenceEquals(c.Identity, table.Identity))) throw new ArgumentException("Conflict target belongs to another table.");
        return new(5, 7, ControlRecord.Vector(_columns.Select(c => c.Definition.Id)));
    }
}
