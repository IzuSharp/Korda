using Korda.Data;
using Korda.Database.Internal;
using Korda.Query;
using Korda.Query.Internal;

namespace Korda.Database;

public sealed partial class DataSession
{
    /// <summary>Loads one bounded parent batch in one native SQL execution and returns index-aligned child groups.</summary>
    /// <typeparam name="TParent">The declared parent entity class.</typeparam>
    /// <typeparam name="TChild">The statically materialized child class.</typeparam>
    /// <param name="parentKeys">Complete immutable keys in page order; repeated keys retain separate result positions.</param>
    /// <param name="relation">The complete immutable primary-key/foreign-key mapping.</param>
    /// <param name="cancellationToken">Requests cancellation and waits for actual cursor retirement.</param>
    /// <returns>One owned child list per input key, including empty lists; no child order is implied.</returns>
    /// <exception cref="ArgumentNullException">An argument or key is null.</exception>
    /// <exception cref="ArgumentException">A key is incomplete, foreign or incompatible with the relation.</exception>
    /// <exception cref="DatabaseException">The input batch, total result, native query or provider exceeds its admitted limits.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed with cursor cleanup.</exception>
    /// <remarks>The collection is snapshotted before dispatch and may contain at most MaxBatchRows keys.
    /// Empty input executes no SQL. Each nonempty input becomes one parameterized native UNION ALL statement;
    /// provider preparation and protocol exchanges are separate from SQL execution count. Native predicates
    /// preserve composite-key and collation semantics. One result stream bounds all groups together, including
    /// duplicate keys; there is no hidden query per parent, parent-existence check, retry or automatic split.</remarks>
    public Task<IReadOnlyList<IReadOnlyList<TChild>>> LoadRelatedAsync<TParent, TChild>(IReadOnlyList<EntityKey<TParent>> parentKeys,
        Relation<TParent, TChild> relation, CancellationToken cancellationToken = default) where TParent : class where TChild : class
    {
        ArgumentNullException.ThrowIfNull(parentKeys); ArgumentNullException.ThrowIfNull(relation);
        relation.RequireComplete(); CheckOpen(); RequireBuffered();
        Source.Capabilities.Require("db.relationships.explicit.v1"); Source.Capabilities.Require("db.query.v1"); Source.Capabilities.Require("db.streaming.v1");
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<IReadOnlyList<TChild>>>(cancellationToken);
        var count = parentKeys.Count;
        if (count > Source.Limits.MaxBatchRows)
            throw new DatabaseException("The parent batch exceeds MaxBatchRows; pass a bounded page or explicit batch.", DatabaseErrorKind.ResourceLimit);
        var keys = new EntityKey<TParent>[count];
        for (var i = 0; i < keys.Length; i++) { var key = parentKeys[i]; MutationEncoding.CheckKey(relation.Parent, key); keys[i] = key; }
        if (count == 0) return Task.FromResult<IReadOnlyList<IReadOnlyList<TChild>>>(Array.Empty<IReadOnlyList<TChild>>());

        var row = relation.Child.Row;
        if (row.Fields.Length == DbWire.MaxDescriptors)
            throw new DatabaseException("The child projection has no capacity for the parent ordinal.", DatabaseErrorKind.ResourceLimit);
        var alias = "__korda_parent_index";
        for (var suffix = 0; row.Fields.Any(field => field.Name == alias); suffix++) alias = "__korda_parent_index_" + suffix;
        var projection = new Projection<IndexedChild<TChild>>([.. row.Fields, ResultField.Create(alias, Codecs.Int32)],
            reader => new(reader.Read(row.Fields.Length, Codecs.Int32), row.Materializer(reader)), relation.Child.Identity);
        var query = From(relation.Child);
        try
        {
            var selection = SelectParent(query, keys[0], relation, projection, 0);
            var capacityHint = keys.Length > 1 ? query.Context.Binding.PrivateBranchCapacityHint : 0;
            for (var i = 1; i < keys.Length; i++)
            {
                using var next = From(relation.Child);
                if (capacityHint > 0 && MatchingKeyLengths(keys[0], keys[i]))
                    next.Context.Binding.ReserveEmptyPrivateBranch(capacityHint);
                selection = selection.CombineConsumed(SelectParent(next, keys[i], relation, projection, i));
            }
            return GroupRelated(query, selection.ToListAsync(cancellationToken), keys.Length);
        }
        catch { query.Dispose(); throw; }
    }

    private static bool MatchingKeyLengths<TParent>(EntityKey<TParent> first, EntityKey<TParent> next) where TParent : class
    {
        if (first.Values.Length != next.Values.Length) return false;
        for (var i = 0; i < first.Values.Length; i++)
            if (first.Values[i].Bytes?.Length != next.Values[i].Bytes?.Length) return false;
        return true;
    }

    private QuerySelection<IndexedChild<TChild>> SelectParent<TParent, TChild>(Query<TChild> query, EntityKey<TParent> key,
        Relation<TParent, TChild> relation, Projection<IndexedChild<TChild>> projection, int index) where TParent : class where TChild : class
        => query.Context.Run(() =>
        {
            var context = query.Context;
            for (var i = 0; i < key.Values.Length; i++)
            {
                var child = relation.Children[i]; var codec = child.Definition.Codec;
                var bytes = DbWire.Encode(new(PayloadKind.ExpressionValues, Source.Binding.SemanticProfile, new byte[32], 0, 0,
                    [new(1, codec, Source.BindCodec(codec))])
                { ExactRows = 1 }, 1, [new ReadOnlyMemory<byte>(key.Values[i].Bytes!)]);
                var predicate = context.Binding.Binary(1, context.Binding.Column(child.Definition.Id), context.Binding.Value(bytes));
                context.Clause(1, QueryContext.U(1, predicate.In(context.Binding)));
            }
            ExpressionReference[] expressions = [.. relation.Child.Columns.Select(column => context.Binding.Column(column.Definition.Id)), context.Value(index, Codecs.Int32).Reference];
            return context.Select(projection, expressions);
        });

    private static async Task<IReadOnlyList<IReadOnlyList<TChild>>> GroupRelated<TChild>(Query<TChild> query,
        Task<IReadOnlyList<IndexedChild<TChild>>> pending, int count) where TChild : class
    {
        using (query)
        {
            var rows = await pending.ConfigureAwait(false);
            var groups = new List<TChild>?[count];
            foreach (var row in rows)
            {
                if ((uint)row.Index >= (uint)count) throw new DatabaseException("A relation parent ordinal differs from the requested batch.", DatabaseErrorKind.ShapeMismatch);
                (groups[row.Index] ??= []).Add(row.Value);
            }
            var result = new IReadOnlyList<TChild>[count];
            for (var i = 0; i < result.Length; i++) result[i] = groups[i] is { } group ? group : Array.Empty<TChild>();
            return result;
        }
    }

    private readonly record struct IndexedChild<TChild>(int Index, TChild Value);
}
