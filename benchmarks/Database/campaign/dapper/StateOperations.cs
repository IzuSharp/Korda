using System.Data;
using System.Data.Common;
using Dapper;
using DbPerf.Campaign;
#if POSTGRESQL
using ProviderConnection = Npgsql.NpgsqlConnection;
#else
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace DbPerf.IndependentOrm;

internal sealed partial class DapperWorker
{
    private static long s_activeTransactions, s_createdTransactions, s_closedTransactions;
    private readonly record struct StateRead(Digest Digest, long Rows, long Suspensions);

    private async ValueTask<Result> ExecuteState(ProviderConnection connection, StatePlan plan)
    {
        DbTransaction? transaction = null;
        var digest = Digest.Create();
        long rows = 0, suspensions = 0;
        var outcome = "success";
        var prepared = _config.Mode == "prepared";
        try
        {
            foreach (var step in plan.Steps)
            {
                var sql = step.Sql(prepared);
                switch (step.Contract.Kind)
                {
                    case "begin":
                        if (transaction is not null) throw new InvalidOperationException("Nested transactions are not in the shared contract.");
#if SQLITE
                        // Match SQL BEGIN's deferred write-lock acquisition. The SQLite
                        // provider's default BeginTransaction is immediate instead.
                        transaction = connection.BeginTransaction(deferred: true);
#else
                        var beginning = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                        if (!beginning.IsCompleted) suspensions++;
                        transaction = await beginning.ConfigureAwait(false);
#endif
                        Interlocked.Increment(ref s_createdTransactions);
                        Interlocked.Increment(ref s_activeTransactions);
                        break;
                    case "commit":
                        if (transaction is null) throw new InvalidOperationException("Commit without a transaction.");
                        var committing = transaction.CommitAsync();
                        if (!committing.IsCompleted) suspensions++;
                        await committing.ConfigureAwait(false);
                        var committedDisposal = DisposeTransactionAsync(transaction);
                        if (!committedDisposal.IsCompleted) suspensions++;
                        await committedDisposal.ConfigureAwait(false);
                        transaction = null;
                        break;
                    case "rollback":
                        if (transaction is null) throw new InvalidOperationException("Rollback without a transaction.");
                        var rollingBack = transaction.RollbackAsync();
                        if (!rollingBack.IsCompleted) suspensions++;
                        await rollingBack.ConfigureAwait(false);
                        var rolledBackDisposal = DisposeTransactionAsync(transaction);
                        if (!rolledBackDisposal.IsCompleted) suspensions++;
                        await rolledBackDisposal.ConfigureAwait(false);
                        transaction = null;
                        break;
                    case "execute":
                        var executing = prepared ? ExecuteStateCommandPrepared(connection, transaction, step)
                            : ExecuteStateCommand(connection, transaction, step, sql);
                        if (!executing.IsCompleted) suspensions++;
                        var affected = await executing.ConfigureAwait(false);
                        if (affected != step.Contract.ExpectedAffected)
                            throw StateMismatch($"Expected {step.Contract.ExpectedAffected} affected rows, received {affected}.");
                        if (affected == 0 && step.Shape == "conflict") outcome = "conflict";
                        break;
                    case "select":
                        var key = step.Key ?? step.NewKey();
                        var stream = prepared ? QueryStatePrepared(connection, transaction, step)
                            : connection.QueryUnbufferedAsync<StateRow>(sql, key, transaction: transaction, commandTimeout: 5);
                        var selected = await ReadState(stream, step, plan.WorkerKey, digest).ConfigureAwait(false);
                        digest = selected.Digest; rows += selected.Rows; suspensions += selected.Suspensions;
                        break;
                    default:
                        throw new NotSupportedException("Unknown transaction operation: " + step.Contract.Kind);
                }
            }
            if (transaction is not null) throw new InvalidOperationException("The business operation left a transaction open.");
            return new(rows, digest.Value, suspensions, outcome);
        }
        catch (Exception original)
        {
            if (transaction is not null)
            {
                Exception? rollbackError = null, disposalError = null;
                try { await transaction.RollbackAsync().ConfigureAwait(false); }
                catch (Exception error) { rollbackError = error; }
                try { await DisposeTransactionAsync(transaction).ConfigureAwait(false); }
                catch (Exception error) { disposalError = error; }
                if (rollbackError is not null || disposalError is not null)
                {
                    var errors = new List<Exception>(3) { original };
                    if (rollbackError is not null) errors.Add(rollbackError);
                    if (disposalError is not null) errors.Add(disposalError);
                    throw new AggregateException("Business operation and transaction cleanup failed.", errors);
                }
            }
            throw;
        }
    }

    private static Task<int> ExecuteStateCommand(ProviderConnection connection, DbTransaction? transaction, StateStep step, string sql)
    {
        // Keep concrete argument types at every intercepted ORM call site.
        if (step.Shape == "set")
        {
            var args = step.Set ?? step.NewSet();
            return connection.ExecuteAsync(sql, args, transaction: transaction, commandTimeout: 5);
        }
        if (step.Shape == "note")
        {
            var args = step.Note ?? step.NewNote();
            return connection.ExecuteAsync(sql, args, transaction: transaction, commandTimeout: 5);
        }
        if (step.Shape == "conflict")
        {
            var args = step.Conflict ?? step.NewConflict();
            return connection.ExecuteAsync(sql, args, transaction: transaction, commandTimeout: 5);
        }
        throw new NotSupportedException("Unknown generated state-command shape.");
    }

    private static async ValueTask<StateRead> ReadState(IAsyncEnumerable<StateRow> stream, StateStep step, long workerKey, Digest digest)
    {
        long rows = 0, suspensions = 0;
        var reader = stream.GetAsyncEnumerator();
        Interlocked.Increment(ref s_createdReaders);
        Interlocked.Increment(ref s_activeReaders);
        Exception? readingError = null;
        try
        {
            while (true)
            {
                var moving = reader.MoveNextAsync();
                if (!moving.IsCompleted) suspensions++;
                if (!await moving.ConfigureAwait(false)) break;
                var value = reader.Current;
                if (value.Id != workerKey || value.Counter != step.Contract.ExpectedCounter ||
                    value.Version != step.Contract.ExpectedVersion || !string.Equals(value.Note, step.Contract.ExpectedNote, StringComparison.Ordinal))
                    throw StateMismatch("State row differs from the declared worker key or expected persisted tuple.");
                digest.Integer(value.Counter); digest.Integer(value.Version); digest.Text(value.Note);
                rows++;
            }
        }
        catch (Exception error) { readingError = error; throw; }
        finally
        {
            try
            {
                var closing = reader.DisposeAsync();
                if (!closing.IsCompleted) suspensions++;
                await closing.ConfigureAwait(false);
                Interlocked.Increment(ref s_closedReaders);
                Interlocked.Decrement(ref s_activeReaders);
            }
            catch (Exception cleanup)
            {
                Interlocked.Increment(ref s_disposalFailures);
                Volatile.Write(ref s_retirementFault, 1);
                if (readingError is not null) throw new AggregateException("State read and enumerator cleanup failed.", readingError, cleanup);
                throw;
            }
        }
        if (rows != 1) throw StateMismatch($"State SELECT expected one row, received {rows}.");
        return new(digest, rows, suspensions);
    }

    private static async ValueTask DisposeTransactionAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            Interlocked.Increment(ref s_closedTransactions);
            Interlocked.Decrement(ref s_activeTransactions);
        }
        catch { Interlocked.Increment(ref s_disposalFailures); Volatile.Write(ref s_retirementFault, 1); throw; }
    }

    private static CampaignException StateMismatch(string message) =>
        new("oracle_mismatch", null, new InvalidOperationException(message));
}
