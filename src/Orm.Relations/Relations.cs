using Korda.Data;
using Korda.Database.Internal;
using Korda.Query.Internal;

namespace Korda.Database;

public sealed partial class DataSource
{
    private readonly Dictionary<string, (byte[] Identity, DatabaseOwner Owner)> _relations = new(StringComparer.Ordinal);
    internal DatabaseOwner RegisterRelation<TParent, TChild>(Relation<TParent, TChild> relation) where TParent : class where TChild : class
    {
        var bytes = relation.CanonicalBytes(); var hash = Canonical.Hash(bytes);
        CheckOpen(); lock (_registrations)
        {
            CheckOpen();
            if (_relations.TryGetValue(hash, out var existing))
            {
                if (!bytes.AsSpan().SequenceEqual(existing.Identity)) throw new ArgumentException("Relation identity hash collision.", nameof(relation));
                return existing.Owner;
            }
            var parent = RegisterModel(relation.Parent); var child = RegisterModel(relation.Child);
            try
            {
                var owner = MetadataBindings.RegisterRelation(Owner, parent, child, relation);
                _relations.Add(hash, (bytes, owner)); return owner;
            }
            catch (BindingFailure failure) { throw HostFailure.Binding(Binding, failure); }
        }
    }
    partial void ReleaseRelations()
    { foreach (var relation in _relations.Values) relation.Owner.Dispose(); _relations.Clear(); }
    internal int RelationCacheCount { get { lock (_registrations) return _relations.Count; } }
}

public sealed partial class DataSession
{
    /// <summary>Snapshots one complete parent key and runs one bounded native child foreign-key query.</summary>
    /// <typeparam name="TParent">The declared parent entity class.</typeparam>
    /// <typeparam name="TChild">The statically materialized child class.</typeparam>
    /// <param name="parentKey">Every non-null primary-key component in declaration order.</param>
    /// <param name="relation">An immutable post-Build relationship description.</param>
    /// <param name="cancellationToken">Requests cancellation and waits for actual cursor retirement.</param>
    /// <returns>An owned bounded child list, without an implicit ordering or parent-existence query.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A key, model, codec or relation shape differs.</exception>
    /// <exception cref="DatabaseException">Capability, capacity, provider or concurrent-use admission fails.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed after native cleanup.</exception>
    /// <remarks>NULL foreign-key components match no parent. Additional tenant filtering must be explicit in a Fluent query.</remarks>
    public Task<IReadOnlyList<TChild>> LoadRelatedAsync<TParent, TChild>(EntityKey<TParent> parentKey, Relation<TParent, TChild> relation, CancellationToken cancellationToken = default) where TParent : class where TChild : class
    {
        ArgumentNullException.ThrowIfNull(relation); MutationEncoding.CheckKey(relation.Parent, parentKey); relation.RequireComplete(); CheckOpen();
        RequireBuffered(); Source.Capabilities.Require("db.relationships.explicit.v1"); Source.Capabilities.Require("db.query.v1"); Source.Capabilities.Require("db.streaming.v1");
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<TChild>>(cancellationToken);
        var key = MutationEncoding.Values(Source, relation.Parent, [parentKey.Values]);
        var projection = relation.Child.Row; var projectionId = Source.RegisterProjection(projection);
        var codecs = projection.Fields.Select(field => Source.BindCodec(field.Descriptor)).ToArray();
        var owner = Source.RegisterRelation(relation);
        var stream = new TypedStream<TChild>(async (rows, token) =>
        {
            CheckOpen();
            var cursor = await Source.Start(() =>
            {
                try { return AdmitOperation(() => MetadataBindings.CreateRelationLoad(Owner, owner, key, projectionId)); }
                catch (BindingFailure failure) { throw HostFailure.Binding(Source.Binding, failure); }
            }, operation => NativeHost.TakeOwner(Source.Binding, operation, 2, DatabaseOwnerKind.Cursor), token).ConfigureAwait(false);
            try { return new HostStreamTransport(Source, cursor, rows, token); } catch { cursor.Dispose(); throw; }
        }, projection, Source.Binding.SemanticProfile, projectionId, codecs, StreamingLimits, cancellationToken);
        return stream.ToListAsync();
    }
    private void RequireBuffered()
    {
        if (Source.Limits.MaxBufferedRows == 0 || Source.Limits.MaxBufferedBytes == 0)
            throw new DatabaseException("Buffered result demand is disabled.", DatabaseErrorKind.ResourceLimit);
    }
}
