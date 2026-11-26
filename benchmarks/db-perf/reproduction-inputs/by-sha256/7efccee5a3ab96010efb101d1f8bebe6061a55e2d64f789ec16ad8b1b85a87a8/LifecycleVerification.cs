using System.Diagnostics;
using Korda.Data;
using Korda.Database;

internal sealed record LifecycleCheck(string Name,string Status,string Detail,long AllocatedBefore,long AllocatedAfter,
    double Seconds,bool? ObservedAsyncSuspension,long? SqlExecutions,string? ExceptionType);
internal sealed record ConcurrentAttempt(string Outcome,long StartedTicks,long FinishedTicks,bool Suspended);
internal sealed record LifecycleReceipt(string Route,string Provider,bool NativeAot,LifecycleCheck[] Checks,
    ConcurrentAttempt[] ConcurrentAttempts,bool ConcurrentIntervalsOverlap,NativeCounts Drained,bool OwnersReturnedToZero,
    string LoadedNativePath,string LoadedNativeSha256,string Scope);

internal static class LifecycleVerification
{
    internal static async Task<LifecycleReceipt> Run(DataSource source,Options options,NativeDiagnostics native)
    {
        var checks=new List<LifecycleCheck>();
        await using(var session=await source.OpenSessionAsync())
        {
            if(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(1 AS bigint)"),Codecs.Int64)!=1)throw new InvalidOperationException("Initial health query failed.");
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel();var beforeSql=native.Read().SqlExecutions;var before=GC.GetTotalAllocatedBytes(true);var began=Stopwatch.GetTimestamp();string? exception=null;
                var task=session.ScalarAsync(SqlCommand.Create("SELECT CAST(1 AS bigint)"),Codecs.Int64,canceled.Token);
                try{await task.ConfigureAwait(false);}catch(OperationCanceledException error){exception=error.GetType().Name;}
                var after=GC.GetTotalAllocatedBytes(true);var elapsed=Stopwatch.GetElapsedTime(began).TotalSeconds;var delta=(long)(native.Read().SqlExecutions-beforeSql);
                if(exception is null||delta!=0)throw new InvalidOperationException("Pre-cancellation executed SQL or did not cancel.");
                checks.Add(new("pre-cancel","passed","Already-canceled normal public ScalarAsync; zero SQL dispatch",before,after,elapsed,!task.IsCompleted,delta,exception));
            }
            using(var cancel=new CancellationTokenSource())
            {
#if POSTGRES
                const string sql="SELECT CAST(1 AS bigint) FROM pg_sleep(2)";
#else
                const string sql="WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<10000000) SELECT sum(v) FROM n";
#endif
                var beforeSql=native.Read().SqlExecutions;var before=GC.GetTotalAllocatedBytes(true);var began=Stopwatch.GetTimestamp();string? exception=null;
                var task=session.ScalarAsync(SqlCommand.Create(sql),Codecs.Int64,cancel.Token);var suspended=!task.IsCompleted;
                await Task.Delay(30).ConfigureAwait(false);
                if(task.IsCompleted)throw new InvalidOperationException("Active-cancel query finished before cancellation was requested.");
                cancel.Cancel();
                try{await task.ConfigureAwait(false);}catch(OperationCanceledException error){exception=error.GetType().Name;}
                var after=GC.GetTotalAllocatedBytes(true);var elapsed=Stopwatch.GetElapsedTime(began).TotalSeconds;var delta=(long)(native.Read().SqlExecutions-beforeSql);
                var state=native.Read();
                if(exception is null||delta!=1||state.Operations!=0||state.Cursors!=0||state.Buffers!=0)
                    throw new InvalidOperationException("Cancellation did not confirm SQL admission and retire cursor/operation/buffer resources.");
                if(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(1 AS bigint)"),Codecs.Int64)!=1)throw new InvalidOperationException("Canceled session was not reusable.");
                checks.Add(new("active-cancel","passed","Cancellation requested after pending real SQL; cursor/operation/buffer retired and session reused",before,after,elapsed,suspended,delta,exception));
            }
        }
        ConcurrentAttempt[] attempts;
        await using(var first=await source.OpenSessionAsync())
        await using(var second=await source.OpenSessionAsync())
        {
            var key=Orders.Table.Key(Orders.TenantId,1L).And(Orders.Id,(long)options.OrdersPerTenant);
            var original=await first.FindAsync(Orders.Table,key)??throw new InvalidOperationException("Missing conflict target.");
            var other=await second.FindAsync(Orders.Table,key);
            if(other!=original||original.Version!=1)throw new InvalidOperationException("Concurrent readers did not start at the same baseline version.");
            var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<ConcurrentAttempt> Attempt(DataSession session)
            {
                await gate.Task.ConfigureAwait(false);var started=Stopwatch.GetTimestamp();
                var pending=session.UpdateCheckedAsync(Orders.Table,key,new Changes<Order>().Set(Orders.Version,2L),new ConcurrencyCheck<Order>().Expect(Orders.Version,1L));
                var suspended=!pending.IsCompleted;
                try
                {
                    if(await pending.ConfigureAwait(false)!=1)throw new InvalidOperationException("Unexpected affected-row result.");
                    return new("updated",started,Stopwatch.GetTimestamp(),suspended);
                }
                catch(ConcurrencyConflictException){return new("conflict",started,Stopwatch.GetTimestamp(),suspended);}
            }
            var one=Attempt(first);var two=Attempt(second);var before=GC.GetTotalAllocatedBytes(true);var began=Stopwatch.GetTimestamp();
            try
            {
                gate.SetResult();attempts=await Task.WhenAll(one,two).ConfigureAwait(false);
                if(attempts.Count(attempt=>attempt.Outcome=="updated")!=1||attempts.Count(attempt=>attempt.Outcome=="conflict")!=1)
                    throw new InvalidOperationException("Concurrent compare-and-swap did not select exactly one winner.");
            }
            finally
            {
                await first.UpdateAsync(Orders.Table,key,new Changes<Order>().Set(Orders.Note,original.Note).Set(Orders.Version,original.Version));
                if(await first.FindAsync(Orders.Table,key)!=original)throw new InvalidOperationException("Concurrent trial did not restore persistent baseline.");
            }
            var after=GC.GetTotalAllocatedBytes(true);var overlap=Math.Max(attempts[0].StartedTicks,attempts[1].StartedTicks)<Math.Min(attempts[0].FinishedTicks,attempts[1].FinishedTicks);
            checks.Add(new("concurrent-optimistic-conflict",overlap?"passed":"not-proven","Two sessions read version1 then concurrently compare-and-swap; one winner, one conflict; persistent row restored",before,after,Stopwatch.GetElapsedTime(began).TotalSeconds,attempts.Any(attempt=>attempt.Suspended),null,null));
        }
        await source.DisposeAsync();var drained=native.Read();var zero=drained is{Sources:0,Sessions:0,Operations:0,Cursors:0,Buffers:0,Owners:0};
        if(!zero)throw new InvalidOperationException("Lifecycle verification retained native owners after drain.");
        return new("korda-public-lifecycle",source.Provider.ToString(),true,checks.ToArray(),attempts,
            Math.Max(attempts[0].StartedTicks,attempts[1].StartedTicks)<Math.Min(attempts[0].FinishedTicks,attempts[1].FinishedTicks),drained,zero,native.Path,native.Sha256,
            "Correctness and exceptional-path allocation observations; all-thread raw bytes include harness/CTS/tasks; no framework-only zero claim or comparative timing acceptance");
    }
}
