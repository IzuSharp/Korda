using Korda.Data;
using Korda.Database.Internal;
using Korda.Query.Internal;

namespace Korda.Database;

public sealed partial class DataSession
{
    /// <summary>Executes one native complete-key SELECT and returns the sole row or null.</summary>
    /// <typeparam name="TEntity">The statically mapped entity class.</typeparam>
    /// <param name="table">The exact immutable table descriptor.</param>
    /// <param name="key">Every declared key component, including any tenant key.</param>
    /// <param name="cancellationToken">Requests cancellation and joins native retirement.</param>
    /// <returns>The owned row or null; duplicate key-shaped results throw.</returns>
    /// <exception cref="ArgumentException">The key is incomplete or belongs to another table.</exception>
    /// <exception cref="InvalidOperationException">The declared key matched multiple rows.</exception>
    /// <exception cref="DatabaseException">Shape, codec, provider or capacity fails.</exception>
    public Task<TEntity?> FindAsync<TEntity>(Table<TEntity> table, EntityKey<TEntity> key, CancellationToken cancellationToken = default) where TEntity : class
    {
        MutationEncoding.CheckKey(table, key); CheckOpen();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<TEntity?>(cancellationToken);
        var plan = Source.AcquireFindPlan(table, key, out var payloadBytes);
        try { return FindCore(plan, plan.Execute(this, key, cancellationToken, payloadBytes)); }
        catch { plan.Dispose(); throw; }
    }
    private static async Task<TEntity?> FindCore<TEntity>(FindPlan<TEntity> plan, Task<TEntity?> task) where TEntity : class
    { using (plan) return await task.ConfigureAwait(false); }

    /// <summary>Snapshots one entity, omits generated columns and executes one native insert.</summary>
    /// <typeparam name="TEntity">The entity class with its direct writer.</typeparam>
    /// <param name="table">The exact immutable table.</param>
    /// <param name="entity">The entity to copy before dispatch; it is never mutated.</param>
    /// <param name="cancellationToken">Requests cancellation without inferring rollback of a dispatched write.</param>
    /// <returns>The provider's affected-row count.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The writer or value shape is invalid.</exception>
    /// <exception cref="DatabaseException">A native constraint, provider or finite capacity fails.</exception>
    public Task<long> InsertAsync<TEntity>(Table<TEntity> table, TEntity entity, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table); CheckOpen();
        var values = MutationEncoding.Values(Source, table, [MutationEncoding.Snapshot(Source, table, entity)]);
        return Mutate(table, 0, MutationEncoding.Record(Source, table, MutationEncoding.Bytes(2, values)), cancellationToken);
    }
    /// <summary>Snapshots one entity and materializes exactly one native INSERT RETURNING row.</summary>
    /// <typeparam name="TEntity">The statically written entity class.</typeparam>
    /// <typeparam name="T">The statically materialized return type.</typeparam>
    /// <param name="table">The exact table.</param>
    /// <param name="entity">The entity snapshot; generated values are omitted.</param>
    /// <param name="returning">Exact existing table columns and codecs in the desired output order.</param>
    /// <param name="cancellationToken">Requests cancellation and joins the returned cursor.</param>
    /// <returns>The single owned returned value.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The returned cardinality differs from one.</exception>
    /// <exception cref="DatabaseException">Capability, output shape, provider or capacity fails.</exception>
    public Task<T> InsertReturningAsync<TEntity, T>(Table<TEntity> table, TEntity entity, Projection<T> returning, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(returning); CheckOpen(); Source.Capabilities.Require("db.returning.v1");
        var values = MutationEncoding.Values(Source, table, [MutationEncoding.Snapshot(Source, table, entity, 20)]);
        var projection = Source.RegisterProjection(returning); var codecs = returning.Fields.Select(f => Source.BindCodec(f.Descriptor)).ToArray();
        var payload = MutationEncoding.Record(Source, table, MutationEncoding.Bytes(2, values), new(7, 1, ControlRecord.U32(projection)));
        var model = Source.RegisterModel(table);
        var stream = new TypedStream<T>((rows, token) =>
        {
            lock (_compoundGate)
            {
                CheckOpen(); token.ThrowIfCancellationRequested();
                var channel = NativeHost.CreateMutationPull(this, model, 1, payload);
                try { return new ValueTask<IStreamTransport>(new HostStreamTransport(Source, channel, rows, token)); }
                catch { channel.Dispose(); throw; }
            }
        }, returning, Source.Binding.SemanticProfile, projection, codecs, StreamingLimits, cancellationToken);
        return stream.SingleAsync();
    }
    /// <summary>Applies only explicit nonkey assignments under the entire native key predicate.</summary>
    /// <typeparam name="TEntity">The statically described entity class.</typeparam>
    /// <param name="table">The exact table.</param>
    /// <param name="key">The complete ordered primary key.</param>
    /// <param name="changes">Owned assignment snapshots; Unset is omitted and nullable Set assigns SQL NULL.</param>
    /// <param name="cancellationToken">Requests cancellation under the existing write-outcome policy.</param>
    /// <returns>Zero or one affected row; multiple matches are a schema constraint failure.</returns>
    /// <exception cref="ArgumentException">The key or assignments are invalid.</exception>
    /// <exception cref="DatabaseException">Provider, schema or capacity fails.</exception>
    public Task<long> UpdateAsync<TEntity>(Table<TEntity> table, EntityKey<TEntity> key, Changes<TEntity> changes, CancellationToken cancellationToken = default) where TEntity : class
        => Update(table, key, changes, null, cancellationToken);
    /// <summary>Executes one key-and-version compare-and-swap without an implicit version increment or replay.</summary>
    /// <typeparam name="TEntity">The statically described entity class.</typeparam>
    /// <param name="table">The exact table.</param>
    /// <param name="key">Every primary-key component, retaining tenant scope.</param>
    /// <param name="changes">Explicit assignments including a caller-supplied new version when desired.</param>
    /// <param name="concurrency">Every declared expected concurrency token.</param>
    /// <param name="cancellationToken">Requests cancellation without fabricating a write outcome.</param>
    /// <returns>Exactly one acknowledged affected row.</returns>
    /// <exception cref="ConcurrencyConflictException">No row matched the exact key and expected tokens.</exception>
    /// <exception cref="ArgumentException">The key, assignments or checks are invalid.</exception>
    /// <exception cref="DatabaseException">The schema, provider or capacity fails.</exception>
    public Task<long> UpdateCheckedAsync<TEntity>(Table<TEntity> table, EntityKey<TEntity> key, Changes<TEntity> changes, ConcurrencyCheck<TEntity> concurrency, CancellationToken cancellationToken = default) where TEntity : class
    { ArgumentNullException.ThrowIfNull(concurrency); return Update(table, key, changes, concurrency, cancellationToken); }
    private Task<long> Update<TEntity>(Table<TEntity> table, EntityKey<TEntity> key, Changes<TEntity> changes, ConcurrencyCheck<TEntity>? concurrency, CancellationToken token) where TEntity : class
    {
        MutationEncoding.CheckKey(table, key); ArgumentNullException.ThrowIfNull(changes); CheckOpen();
        var fields = new List<ControlField> { MutationEncoding.Bytes(2, MutationEncoding.Values(Source, table, [changes.Snapshot(table)])), MutationEncoding.Bytes(3, MutationEncoding.Values(Source, table, [key.Values])) };
        if (concurrency is not null) fields.Add(MutationEncoding.Bytes(4, MutationEncoding.Values(Source, table, [concurrency.Snapshot(table)])));
        return Mutate(table, concurrency is null ? 2u : 3u, MutationEncoding.Record(Source, table, [.. fields]), token);
    }
    /// <summary>Deletes by the entire native primary-key predicate with no read-before-write query.</summary>
    /// <typeparam name="TEntity">The statically described entity class.</typeparam>
    /// <param name="table">The exact table.</param>
    /// <param name="key">The complete ordered primary key.</param>
    /// <param name="cancellationToken">Requests cancellation under the existing transaction policy.</param>
    /// <returns>Zero or one affected row; multiple matches are a schema constraint failure.</returns>
    /// <exception cref="ArgumentException">The key is invalid.</exception>
    /// <exception cref="DatabaseException">Provider, constraint or capacity fails.</exception>
    public Task<long> DeleteAsync<TEntity>(Table<TEntity> table, EntityKey<TEntity> key, CancellationToken cancellationToken = default) where TEntity : class => Delete(table, key, null, cancellationToken);
    /// <summary>Deletes once using the complete key and every explicit expected concurrency token.</summary>
    /// <typeparam name="TEntity">The statically described entity class.</typeparam>
    /// <param name="table">The exact table.</param>
    /// <param name="key">The complete ordered primary key.</param>
    /// <param name="concurrency">The complete expected-token snapshot.</param>
    /// <param name="cancellationToken">Requests cancellation and waits for native retirement.</param>
    /// <returns>Exactly one acknowledged affected row.</returns>
    /// <exception cref="ConcurrencyConflictException">No row matched the key and expected tokens.</exception>
    /// <exception cref="DatabaseException">Provider, schema or capacity fails.</exception>
    public Task<long> DeleteCheckedAsync<TEntity>(Table<TEntity> table, EntityKey<TEntity> key, ConcurrencyCheck<TEntity> concurrency, CancellationToken cancellationToken = default) where TEntity : class
    { ArgumentNullException.ThrowIfNull(concurrency); return Delete(table, key, concurrency, cancellationToken); }
    private Task<long> Delete<TEntity>(Table<TEntity> table, EntityKey<TEntity> key, ConcurrencyCheck<TEntity>? concurrency, CancellationToken token) where TEntity : class
    {
        MutationEncoding.CheckKey(table, key); CheckOpen();
        var fields = new List<ControlField> { MutationEncoding.Bytes(3, MutationEncoding.Values(Source, table, [key.Values])) };
        if (concurrency is not null) fields.Add(MutationEncoding.Bytes(4, MutationEncoding.Values(Source, table, [concurrency.Snapshot(table)])));
        return Mutate(table, concurrency is null ? 4u : 5u, MutationEncoding.Record(Source, table, [.. fields]), token);
    }
    /// <summary>Executes one atomic native upsert with independent insert and conflict-update snapshots.</summary>
    /// <typeparam name="TEntity">The statically described entity class.</typeparam>
    /// <param name="table">The exact table.</param>
    /// <param name="entity">The insert row, excluding generated columns.</param>
    /// <param name="conflict">An explicit column or PostgreSQL constraint target.</param>
    /// <param name="update">A separate nonempty assignment snapshot; values may differ from the inserted row.</param>
    /// <param name="cancellationToken">Requests cancellation under the existing write-outcome policy.</param>
    /// <returns>The provider's affected-row count.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="DatabaseCapabilityException">The target is unsupported by this provider.</exception>
    /// <exception cref="DatabaseException">The actual unique constraint, provider or capacity fails.</exception>
    public Task<long> UpsertAsync<TEntity>(Table<TEntity> table, TEntity entity, ConflictTarget<TEntity> conflict, Changes<TEntity> update, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(conflict); ArgumentNullException.ThrowIfNull(update); CheckOpen(); Source.Capabilities.Require("db.upsert.v1");
        var assignments = MutationEncoding.Values(Source, table, [update.Snapshot(table)]); var target = conflict.Encode(table, Source);
        var insert = MutationEncoding.Snapshot(Source, table, entity, 32L + assignments.Length + target.Bytes.Length);
        return Mutate(table, 6, MutationEncoding.Record(Source, table, MutationEncoding.Bytes(2, MutationEncoding.Values(Source, table, [insert])), target, MutationEncoding.Bytes(8, assignments)), cancellationToken);
    }
    private Task<long> Mutate<TEntity>(Table<TEntity> table, uint kind, byte[] payload, CancellationToken token, BulkSessionLease? bulk = null) where TEntity : class
    { var model = Source.RegisterModel(table); return SubmitCommand(NativeCommandKind.Mutation, model, payload, kind, 100, NativeCommand.Count, token, bulk); }
}
