using Korda.Data;
using Korda.Database.Internal;
using Korda.Query.Internal;

namespace Korda.Database;

/// <summary>Owns one physical native session with fail-fast single-command admission.</summary>
/// <remarks>A cursor and transaction control retain the same session gate. A session is not a concurrent command queue.
/// Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.</remarks>
public sealed partial class DataSession : IDisposable, IAsyncDisposable
{
    internal DatabaseOwner Owner { get; }
    private readonly CloseController _close;
    private int _closing;
    private DataTransaction? _transaction;
    internal DataSession(DataSource source, DatabaseOwner owner)
    { Source = source; Owner = owner; _close = new(source, owner, owner.Dispose); }
    /// <summary>Gets the creating data source, retained while this physical session remains owned.</summary>
    public DataSource Source { get; }
    /// <summary>Gets the last active transaction, or null after its native state has closed.</summary>
    public DataTransaction? Transaction => _transaction is { State: not TransactionState.Closed } transaction ? transaction : null;
    /// <summary>Gets the current cleanup observation, retained after disposal.</summary>
    public DatabaseCloseResult Cleanup => _close.Snapshot;
    internal void CheckOpen() { ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this); Source.CheckOpen(); CheckCompoundUse(); }
    partial void CheckCompoundUse();
    /// <summary>Executes one trusted mutation or explicit session-changing command and observes its affected-row count.</summary>
    /// <param name="command">An immutable typed command with a nonquery effect classification.</param>
    /// <param name="cancellationToken">Requests cancellation; accepted work retains resources until native retirement.</param>
    /// <returns>The exact affected-row count.</returns>
    /// <exception cref="ArgumentException">The command kind is incompatible.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Session or source admission is closed.</exception>
    /// <exception cref="DatabaseException">The provider, capacity or concurrent-use policy rejects execution.</exception>
    public Task<long> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken = default)
    {
        SqlSubmission.Validate(command, 0); CheckOpen();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<long>(cancellationToken);
        return ExecuteCommand(command, cancellationToken);
    }
    private async Task<long> ExecuteCommand(SqlCommand command, CancellationToken token)
    { using var plan = SqlSubmission.Create(this, command, 0, 0); return await ExecuteCountAsync(plan, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false); }
    internal Task<long> ExecuteCountAsync(DatabaseOwner plan, ReadOnlyMemory<byte> values, CancellationToken token)
        => SubmitCommand(NativeCommandKind.Execute, plan, values, 0, 100, NativeCommand.Count, token);
    /// <summary>Reads exactly one row and column through an explicit scalar codec.</summary>
    /// <typeparam name="T">The owned scalar type, including an explicitly nullable codec.</typeparam>
    /// <param name="command">An immutable query command.</param>
    /// <param name="codec">The exact scalar codec; no first-column truncation is performed.</param>
    /// <param name="cancellationToken">The native execution cancellation token.</param>
    /// <returns>The sole scalar value.</returns>
    /// <exception cref="ArgumentNullException">The command or codec is null.</exception>
    /// <exception cref="ArgumentException">The command kind is incompatible.</exception>
    /// <exception cref="InvalidOperationException">Zero or multiple rows are returned.</exception>
    /// <exception cref="DatabaseException">The complete shape, codec, provider or capacity fails.</exception>
    public Task<T> ScalarAsync<T>(SqlCommand command, ValueCodec<T> codec, CancellationToken cancellationToken = default)
    {
        SqlSubmission.Validate(command, 1); ArgumentNullException.ThrowIfNull(codec); CheckOpen();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        return CreateStream(command, Projection<T>.Scalar(codec), 1, cancellationToken).SingleAsync();
    }
    /// <summary>Materializes a bounded list using the complete declared row shape.</summary>
    /// <typeparam name="T">The statically mapped owned result type.</typeparam>
    /// <param name="command">An immutable query command.</param>
    /// <param name="projection">Exact ordered aliases, codecs and a synchronous materializer.</param>
    /// <param name="cancellationToken">The native execution cancellation token.</param>
    /// <returns>An owned list within both row and encoded-byte budgets.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The command kind is incompatible.</exception>
    /// <exception cref="DatabaseException">Shape, conversion, provider or configured capacity fails.</exception>
    public Task<IReadOnlyList<T>> QueryAsync<T>(SqlCommand command, Projection<T> projection, CancellationToken cancellationToken = default)
    {
        SqlSubmission.Validate(command, 2); ArgumentNullException.ThrowIfNull(projection); CheckOpen();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<T>>(cancellationToken);
        if (Source.Limits.MaxBufferedRows == 0 || Source.Limits.MaxBufferedBytes == 0) return Task.FromException<IReadOnlyList<T>>(new DatabaseException("Buffered result demand is disabled by the source limits.", DatabaseErrorKind.ResourceLimit));
        return CreateStream(command, projection, 2, cancellationToken).ToListAsync();
    }
    /// <summary>Creates a one-shot bounded stream; the first MoveNext admits native execution.</summary>
    /// <typeparam name="T">The statically mapped owned result type.</typeparam>
    /// <param name="command">An immutable query command.</param>
    /// <param name="projection">Exact ordered result shape and synchronous materializer.</param>
    /// <param name="cancellationToken">Combined with the enumeration token for native execution.</param>
    /// <returns>A one-shot stream whose enumerator must be disposed, including on early exit.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The command kind is incompatible.</exception>
    /// <exception cref="ObjectDisposedException">Session or source admission is closed.</exception>
    public IAsyncEnumerable<T> StreamAsync<T>(SqlCommand command, Projection<T> projection, CancellationToken cancellationToken = default)
    { SqlSubmission.Validate(command, 3); ArgumentNullException.ThrowIfNull(projection); CheckOpen(); return CreateStream(command, projection, 3, cancellationToken); }
    private TypedStream<T> CreateStream<T>(SqlCommand command, Projection<T> projection, uint resultKind, CancellationToken token)
    {
        var projectionId = Source.RegisterProjection(projection); var bound = projection.Fields.Select(field => Source.BindCodec(field.Descriptor)).ToArray();
        return new((rows, ct) => OpenSqlStream(command, resultKind, projectionId, rows, ct), projection, Source.Binding.SemanticProfile, projectionId, bound, StreamingLimits, token);
    }
    private ValueTask<IStreamTransport> OpenSqlStream(SqlCommand command, uint kind, uint projectionId, int rows, CancellationToken token)
    {
        CheckOpen(); token.ThrowIfCancellationRequested(); using var plan = SqlSubmission.Create(this, command, kind, projectionId);
        return OpenPlanStreamAsync(plan, ReadOnlyMemory<byte>.Empty, rows, token);
    }
    internal StreamLimits StreamingLimits => new(Source.Limits.MaxBatchRows, checked((int)Source.Limits.MaxBatchBytes), Math.Max(1, Source.Limits.MaxBufferedRows), Math.Max(1, Source.Limits.MaxBufferedBytes));
    internal ValueTask<IStreamTransport> OpenPlanStreamAsync(DatabaseOwner plan, ReadOnlyMemory<byte> values, int rows, CancellationToken token)
    {
        lock (_compoundGate)
        {
            CheckOpen(); token.ThrowIfCancellationRequested();
            var channel = NativeHost.CreatePull(this, plan, values);
            try { return new(new HostStreamTransport(Source, channel, rows, token)); }
            catch { channel.Dispose(); throw; }
        }
    }
    /// <summary>Begins a native transaction using the provider's default isolation.</summary>
    /// <param name="cancellationToken">Controls admission before BEGIN dispatch.</param>
    /// <returns>The native transaction owner.</returns>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="DatabaseException">The provider or single-command admission rejects BEGIN.</exception>
    public Task<DataTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var record = DefaultTransactionRecord.Bytes; CheckOpen(); Source.Capabilities.Require("db.transactions.v1");
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<DataTransaction>(cancellationToken);
        return BeginCore(record, null, cancellationToken);
    }
    /// <summary>Begins an exact native transaction without weakening unsupported isolation or read-only policy.</summary>
    /// <param name="options">Immutable isolation, effect and independent commit/cleanup deadline settings.</param>
    /// <param name="cancellationToken">Controls admission before BEGIN dispatch.</param>
    /// <returns>The transaction owner, whose outcome is independent from local cleanup.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is invalid.</exception>
    /// <exception cref="DatabaseException">The provider or admission fails.</exception>
    public Task<DataTransaction> BeginTransactionAsync(TransactionOptions options, CancellationToken cancellationToken = default)
    {
        var record = HostEncoding.Transaction(options); CheckOpen(); Source.Capabilities.Require("db.transactions.v1");
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<DataTransaction>(cancellationToken);
        return BeginCore(record, options.RecoveryKey, cancellationToken);
    }
    private static class DefaultTransactionRecord
    {
        internal static readonly byte[] Bytes;
        // Explicit initialization keeps the one default frame lazy and shared.
        // The native BEGIN factory reads it synchronously and captures values.
        static DefaultTransactionRecord() => Bytes = HostEncoding.Transaction(new TransactionOptions());
    }
    private async Task<DataTransaction> BeginCore(byte[] record, string? recovery, CancellationToken token, BulkSessionLease? bulk = null)
    {
        var owner = await SubmitCommand(NativeCommandKind.BeginTransaction, null, record, 0, 3, NativeCommand.Owner, token, bulk).ConfigureAwait(false);
        try { var transaction = new DataTransaction(new HostTransactionTransport(Source, owner, recovery), recovery); bulk?.AttachTransaction(transaction); Volatile.Write(ref _transaction, transaction); return transaction; }
        catch { owner.Dispose(); throw; }
    }
    /// <summary>Starts closure, then waits within an independent finite caller budget.</summary>
    /// <param name="waitBudget">A nonnegative finite wait; zero observes without waiting.</param>
    /// <param name="cancellationToken">Cancels only the wait after native cleanup starts.</param>
    /// <returns>A truthful snapshot; timeout cannot imply native retirement.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget is invalid.</exception>
    /// <exception cref="OperationCanceledException">The caller stops waiting.</exception>
    public Task<DatabaseCloseResult> CloseAsync(TimeSpan waitBudget, CancellationToken cancellationToken = default)
    { HostEncoding.WaitBudget(waitBudget); RequestClose(); return _close.WaitAsync(waitBudget, cancellationToken); }
    private void RequestClose() { Interlocked.Exchange(ref _closing, 1); _close.Request(); }
    /// <summary>Closes admission and transfers cleanup to the bounded native owner without blocking.</summary>
    public void Dispose() => RequestClose();
    /// <summary>Closes admission and joins actual native local safety.</summary>
    /// <returns>The asynchronous safety join.</returns>
    public ValueTask DisposeAsync() { RequestClose(); return new(_close.JoinAsync()); }
}
