using NativeWeave.Data;
using NativeWeave.Query;
using NativeWeave.Query.Internal;

namespace NativeWeave.Database;

public sealed partial class DataSession
{
    /// <summary>Creates one mutable native SeaQuery arena bound to this session and exact static table.</summary>
    /// <typeparam name="TEntity">The statically described entity class.</typeparam>
    /// <param name="table">The immutable manual or generated table descriptor.</param>
    /// <returns>A query to dispose after its terminal or enumeration completes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Session or source admission is closed.</exception>
    /// <exception cref="DatabaseException">The native metadata, capability or capacity rejects construction.</exception>
    public Query<TEntity> From<TEntity>(Table<TEntity> table) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table); CheckOpen(); Source.Capabilities.Require("db.query.v1");
        var model = Source.RegisterModel(table);
        try { return new(new(this, table.Identity, QueryBinding.Create(Owner, model, table.Identity.Id)), table); }
        catch (BindingFailure failure) { throw HostFailure.Binding(Source.Binding, failure); }
    }
}
public sealed partial class DataSource
{
    private long _queryParameterSequence;
    internal uint AllocateQueryParameter()
    {
        CheckOpen(); var value = Interlocked.Increment(ref _queryParameterSequence);
        if (value <= 0 || value > uint.MaxValue) throw new DatabaseException("Source parameter identity capacity exhausted.", DatabaseErrorKind.ResourceLimit);
        return (uint)value;
    }
}
