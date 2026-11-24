using System.Data.Common;
using System.Diagnostics;
using Dapper;

internal sealed record LifecycleCheck(string Name,string Status,string Detail,long AllocatedBefore,long AllocatedAfter,
    double Seconds,bool? ObservedAsyncSuspension,long? SqlExecutions,string? ExceptionType);
internal sealed record ConcurrentAttempt(string Outcome,long StartedTicks,long FinishedTicks,bool Suspended);
internal sealed record LifecycleReceipt(string Route,string Provider,bool NativeAot,LifecycleCheck[] Checks,
    ConcurrentAttempt[] ConcurrentAttempts,bool ConcurrentIntervalsOverlap,OwnershipCounts Drained,bool ApplicationConnectionsReturnedToZero,
    string? LoadedNativePath,string? LoadedNativeSha256,string Scope);

internal static class LifecycleVerification
{
    private const string FindSql="SELECT \"TenantId\",\"Id\",\"CreatedAt\",\"CustomerId\",\"Total\",\"Note\",\"Version\",\"Settings\",\"InternalMemo\" FROM orders WHERE \"TenantId\"=@tenant AND \"Id\"=@id";
    internal static async Task<LifecycleReceipt> Run(DatabaseDriver source,Options options)
    {
        var checks=new List<LifecycleCheck>();var connection=await source.OpenSessionAsync();
        try
        {
            if(await connection.ExecuteScalarAsync<long>("SELECT CAST(1 AS bigint)")!=1)throw new InvalidOperationException("Initial health query failed.");
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel();var before=GC.GetTotalAllocatedBytes(true);var began=Stopwatch.GetTimestamp();string? exception=null;
                var task=connection.ExecuteScalarAsync<long>("SELECT CAST(@value AS bigint)",new{value=1L,cancellation=canceled.Token});
                try{await task.ConfigureAwait(false);}catch(OperationCanceledException error){exception=error.GetType().Name;}
                var after=GC.GetTotalAllocatedBytes(true);var elapsed=Stopwatch.GetElapsedTime(began).TotalSeconds;
                if(exception is null)throw new InvalidOperationException("Pre-cancellation was not observed by the provider.");
                checks.Add(new("pre-cancel","passed","Already-canceled Dapper.AOT scalar query; internal SQL dispatch count unavailable",before,after,elapsed,!task.IsCompleted,null,exception));
            }
            using(var cancel=new CancellationTokenSource())
            {
#if POSTGRES
                const string sql="SELECT CAST(@value AS bigint) FROM pg_sleep(2)";
#else
                const string sql="WITH RECURSIVE n(v) AS (VALUES(@value) UNION ALL SELECT v+1 FROM n WHERE v<10000000) SELECT sum(v) FROM n";
#endif
                var before=GC.GetTotalAllocatedBytes(true);var began=Stopwatch.GetTimestamp();string? exception=null;
                cancel.CancelAfter(30);
                var task=connection.ExecuteScalarAsync<long>(sql,new{value=1L,cancellation=cancel.Token});var suspended=!task.IsCompleted;
                try{await task.ConfigureAwait(false);}catch(OperationCanceledException error){exception=error.GetType().Name;}
                var after=GC.GetTotalAllocatedBytes(true);var elapsed=Stopwatch.GetElapsedTime(began).TotalSeconds;
                if(await connection.ExecuteScalarAsync<long>("SELECT CAST(1 AS bigint)")!=1)throw new InvalidOperationException("Connection was not reusable after cancellation trial.");
#if POSTGRES
                if(exception is null||!suspended)throw new InvalidOperationException("Active PostgreSQL cancellation was not observed on a suspended call.");
                checks.Add(new("active-cancel","passed","Npgsql suspended query canceled through generated Dapper cancellation token; connection reused",before,after,elapsed,suspended,null,exception));
#else
                checks.Add(new("active-cancel","unsupported","Microsoft.Data.Sqlite executes database work synchronously; timed token did not establish engine interruption. Observed outcome and duration retained, no cancellation-equivalence claim",before,after,elapsed,suspended,null,exception));
#endif
            }
        }
        finally{await source.CloseSessionAsync(connection);}
        var first=await source.OpenSessionAsync();var second=await source.OpenSessionAsync();ConcurrentAttempt[] attempts;
        try
        {
            var key=new{tenant=1L,id=(long)options.OrdersPerTenant};var original=await first.QuerySingleAsync<Order>(FindSql,key);
            var other=await second.QuerySingleAsync<Order>(FindSql,key);
            if(other!=original||original.Version!=1)throw new InvalidOperationException("Concurrent readers did not share initial version1.");
            using var barrier=new Barrier(3);
            Task<ConcurrentAttempt> Attempt(DbConnection session)=>Task.Factory.StartNew(()=>
            {
                barrier.SignalAndWait();var started=Stopwatch.GetTimestamp();
                var pending=session.ExecuteAsync("UPDATE orders SET \"Version\"=@version WHERE \"TenantId\"=@tenant AND \"Id\"=@id AND \"Version\"=@expected",
                    new{tenant=1L,id=key.id,version=2L,expected=1L});var suspended=!pending.IsCompleted;
                var changed=pending.GetAwaiter().GetResult();
                if(changed is not(0 or 1))throw new InvalidOperationException("Unexpected compare-and-swap row count.");
                return new ConcurrentAttempt(changed==1?"updated":"conflict",started,Stopwatch.GetTimestamp(),suspended);
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            var one=Attempt(first);var two=Attempt(second);var before=GC.GetTotalAllocatedBytes(true);var began=Stopwatch.GetTimestamp();
            try
            {
                barrier.SignalAndWait();attempts=await Task.WhenAll(one,two).ConfigureAwait(false);
                if(attempts.Count(attempt=>attempt.Outcome=="updated")!=1||attempts.Count(attempt=>attempt.Outcome=="conflict")!=1)
                    throw new InvalidOperationException("Concurrent compare-and-swap did not select one winner.");
            }
            finally
            {
                await first.ExecuteAsync("UPDATE orders SET \"Note\"=@note,\"Version\"=@version WHERE \"TenantId\"=@tenant AND \"Id\"=@id",
                    new{tenant=1L,id=key.id,note=original.Note,version=original.Version});
                if(await first.QuerySingleAsync<Order>(FindSql,key)!=original)throw new InvalidOperationException("Conflict trial did not restore persistent baseline.");
            }
            var after=GC.GetTotalAllocatedBytes(true);var overlap=Math.Max(attempts[0].StartedTicks,attempts[1].StartedTicks)<Math.Min(attempts[0].FinishedTicks,attempts[1].FinishedTicks);
            checks.Add(new("concurrent-optimistic-conflict",overlap?"passed":"not-proven","Two dedicated callers start together; version predicate plus application affected-row check selects one winner; persistent state restored",before,after,Stopwatch.GetElapsedTime(began).TotalSeconds,attempts.Any(attempt=>attempt.Suspended),null,null));
        }
        finally{await source.CloseSessionAsync(first);await source.CloseSessionAsync(second);}
        await source.DisposeAsync();var drained=source.Read();var zero=drained.ApplicationConnections==0;
        if(!zero)throw new InvalidOperationException("Application retained logical provider connections.");
        return new("dapper-aot-lifecycle",source.Provider,true,checks.ToArray(),attempts,
            Math.Max(attempts[0].StartedTicks,attempts[1].StartedTicks)<Math.Min(attempts[0].FinishedTicks,attempts[1].FinishedTicks),drained,zero,source.NativePath,source.NativeHash,
            "Correctness and exceptional-path all-thread allocation observations; internal native ownership and SQL dispatch counts unavailable; unsupported capability remains explicit");
    }
}
