using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Interop.Abi;
using Korda.Query.Internal;

internal static class BatchResultTests
{
    private static NativeBatchResult _next;
    private static uint _status;
    private static int _takes, _views, _releases, _checks;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint Take(nint operation, NativeBatchResult* result, NwErrorInfoV1* error)
    { _takes++; *result = _next; return _status; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(nint owner) => _releases++;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint View(nint owner, NwBytesViewV1* result, NwErrorInfoV1* error)
    { _views++; return NativeStatus.ContractViolation; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint CheckRead(nint owner, NwErrorInfoV1* error) => NativeStatus.Ok;

    internal static unsafe void VerifyRecord()
    {
        NativeBatchResult layout = default;
        Check(sizeof(NativeBatchResult) == 32 && (byte*)&layout.Owner - (byte*)&layout == 0
            && (byte*)&layout.Bytes - (byte*)&layout == 8 && (byte*)&layout.KnownEnd - (byte*)&layout == 24
            && (byte*)&layout.Reserved - (byte*)&layout == 28, "C# batch result x64 layout differs from the additive C record");
        var calls = new NativeCalls("batch-result-test", symbol => symbol switch
        {
            "nw_db_buffer_release_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Release,
            "nw_db_buffer_view_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint>)&View,
            _ => 0
        });
        var binding = new SourceBinding(calls, 2)
        {
            Batches = new(symbol => symbol switch
            {
                "nw_db_operation_take_batch_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, NativeBatchResult*, NwErrorInfoV1*, uint>)&Take,
                "nw_db_cursor_check_read_admission_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, NwErrorInfoV1*, uint>)&CheckRead,
                _ => 0
            })
        };
        byte* bytes = stackalloc byte[96]; new Span<byte>(bytes, 96).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(bytes + 56, 4), 1);
        foreach (var end in new uint[] { 0, 1 })
        {
            Reset(bytes); _next.KnownEnd = end;
            var batch = NativeHost.TakeBatch(binding, 17, 96);
            var owner = batch.ReadOwner; var retained = false;
            try
            {
                owner.DangerousAddRef(ref retained);
                Check(batch.Bytes.Span.Length == 96 && batch.End == (end == 1), "The take result lost its borrowed view or known EOF");
            }
            finally { if (retained) owner.DangerousRelease(); }
            batch.Dispose(); batch.Dispose();
            Check(_takes == 1 && _views == 0 && _releases == 1, "A batch must use one take, no buffer_view and one release");
        }
        foreach (var fault in new[] { "null-view", "oversized", "short-header", "end-flag", "reserved", "failed-owner" })
        {
            Reset(bytes);
            switch (fault)
            {
                case "null-view": _next.Bytes.Data = null; break;
                case "oversized": _next.Bytes.Length = 97; break;
                case "short-header": _next.Bytes.Length = 95; break;
                case "end-flag": _next.KnownEnd = 2; break;
                case "reserved": _next.Reserved = 1; break;
                case "failed-owner": _status = NativeStatus.InvalidState; break;
            }
            if (fault == "failed-owner") Reject<InvalidOperationException>(() => NativeHost.TakeBatch(binding, 17, 96));
            else Reject<DatabaseException>(() => NativeHost.TakeBatch(binding, 17, 96));
            Check(_takes == 1 && _views == 0 && _releases == 1, "Malformed result did not compensate its owned reference: " + fault);
        }
        Reset(bytes); _next.Owner = 0;
        Reject<InvalidOperationException>(() => NativeHost.TakeBatch(binding, 17, 96));
        Check(_releases == 0, "A null output must not be released as an owned reference");
        binding.Batches = new(_ => 0);
        Reject<NotSupportedException>(() => binding.Batches.Require());
        binding.Batches = new(symbol => symbol == "nw_db_operation_take_batch_v1"
            ? (nint)(delegate* unmanaged[Cdecl]<nint, NativeBatchResult*, NwErrorInfoV1*, uint>)&Take : 0);
        Reject<NotSupportedException>(() => binding.Batches.Require());
        Console.WriteLine($"BATCH_RESULT_RECORD_PASS assertions={_checks}; x64 layout, one-call view, malformed ownership and missing export");
    }

    private static unsafe void Reset(byte* bytes)
    {
        _next = new() { Owner = 17, Bytes = new() { Data = bytes, Length = 96 } };
        _status = NativeStatus.Ok; _takes = _views = _releases = 0;
    }

    internal static async Task VerifyFetches(DatabaseProvider provider, string connection)
    {
        var beforeChecks = _checks;
        var builder = new DataSourceBuilder().ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 32, MaxBatchBytes = 65536 });
        if (provider == DatabaseProvider.Sqlite) builder.UseSqlite(connection);
        else builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        await using var source = builder.Build();
        await using var session = await source.OpenSessionAsync();
        var retirements = 0;
        source.TestRetirement = snapshot =>
        {
            Check(snapshot.Released && snapshot.NativeQuiet, "A counted query operation returned before native retirement");
            Interlocked.Increment(ref retirements);
        };
        var projection = Projection<Item>.Create([ResultField.Create("id", Codecs.Int64)], row => new(row.Read(0, Codecs.Int64)));
        foreach (var count in new[] { 0, 1, 32, 64, 100, 1000 })
        {
            var command = SqlCommand.Create("WITH RECURSIVE items(id) AS (SELECT CAST(1 AS BIGINT) WHERE @count > 0 UNION ALL SELECT id + 1 FROM items WHERE id < @count) SELECT id FROM items ORDER BY id",
                new SqlParameters().Add("count", (long)count, Codecs.Int64));
            var before = Volatile.Read(ref retirements); var rows = 0; long sum = 0;
            await foreach (var item in session.StreamAsync(command, projection)) { rows++; sum += item.Id; }
            var expectedFetches = count / 32 + 1;
            Check(rows == count && sum == (long)count * (count + 1) / 2, "The public batch stream changed its business result");
            Check(Volatile.Read(ref retirements) - before == expectedFetches + 2,
                "The public stream must retire one execute, its required fetches and one close; known EOF must not fetch again");
            Console.WriteLine($"BATCH_RESULT_FETCH_PASS provider={provider} rows={count} fetches={expectedFetches} retiredOperations={Volatile.Read(ref retirements) - before}");
        }
        Console.WriteLine($"BATCH_RESULT_FETCH_PASS provider={provider} assertions={_checks - beforeChecks}; partial EOF and exact row boundaries");
    }

    internal static async Task VerifyParentTransactions(DatabaseProvider provider, string connection)
    {
        var beforeChecks = _checks;
        foreach (var batchRows in new[] { 1, 2 })
        {
            var builder = new DataSourceBuilder().ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = batchRows });
            if (provider == DatabaseProvider.Sqlite) builder.UseSqlite(connection);
            else builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
            await using var source = builder.Build();
            await using var session = await source.OpenSessionAsync();
            foreach (var control in new[] { "commit", "rollback", "dispose" })
            {
                await using var transaction = await session.BeginTransactionAsync();
                var projection = Projection<Item>.Create([ResultField.Create("id", Codecs.Int64)], row => new(row.Read(0, Codecs.Int64)));
                await using var reader = session.StreamAsync(SqlCommand.Create("SELECT CAST(1 AS BIGINT) AS id"), projection).GetAsyncEnumerator();
                Check(await reader.MoveNextAsync() && reader.Current.Id == 1, "The active transaction delivered its row");
                Task? closing = null;
                if (control == "dispose")
                {
                    closing = transaction.DisposeAsync().AsTask();
                    Check(!closing.IsCompleted, "Transaction close waits for the admitted cursor's command gate");
                }
                else if (control == "commit")
                {
                    var failure = await Failure<DatabaseException>(transaction.CommitAsync());
                    Check(failure.Kind == DatabaseErrorKind.ConcurrentUse && transaction.State == TransactionState.Active,
                        "Commit while a cursor owns the gate rejects without closing that transaction");
                }
                else
                {
                    var failure = await Failure<DatabaseCleanupException>(transaction.RollbackAsync());
                    Check(failure.Data["Korda.Database.Primary.v1"] is DatabaseException { Kind: DatabaseErrorKind.ConcurrentUse }
                        && transaction.State == TransactionState.Active,
                        "Busy rollback retains its cleanup wrapper and original concurrent-use diagnostic");
                }
                Check(!await reader.MoveNextAsync(), "An existing cursor may reach EOF while transaction close waits or control admission was rejected");
                await reader.DisposeAsync();
                if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(10));
                else await transaction.RollbackAsync();
                await transaction.DisposeAsync();
                Check(transaction.Outcome == TransactionOutcome.RolledBack && transaction.Cleanup.LocalResourcesReleased,
                    "The parent transaction retires after the cursor, with confirmed rollback");
                Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(7 AS BIGINT) AS id"), Codecs.Int64) == 7,
                    "The same physical session is reusable after cursor and transaction retirement");
                Console.WriteLine($"BATCH_RESULT_PARENT_PASS provider={provider} batchRows={batchRows} control={control}; cursor completed and transaction retired");
            }
        }
        Console.WriteLine($"BATCH_RESULT_PARENT_PASS provider={provider} assertions={_checks - beforeChecks}; transaction close/control preserves unknown and known EOF behavior");
    }

    private static async Task<T> Failure<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (T failure) { Check(true, "Observed expected " + typeof(T).Name); return failure; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Check(bool value, string message)
    { Interlocked.Increment(ref _checks); if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed record Item(long Id);
}
