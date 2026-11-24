using System.Diagnostics;
using System.Security.Cryptography;

// Null counters are unobservable, not zero. The independent ORM exposes only
// application-owned logical connections; Korda also exposes native ownership.
internal sealed record RecoveryOwners(long? Sources,long Connections,long? Operations,long? Cursors,
    long? Buffers,long? Owners,long? SqlExecutions,long? ProtocolSubmissions,string Scope)
{
    internal bool MatchesQuiescent(RecoveryOwners warm) => Sources==warm.Sources && Connections==warm.Connections
        && Owners==warm.Owners && (Operations is null or 0) && (Cursors is null or 0) && (Buffers is null or 0);
    internal bool AllTrackedZero => Connections==0 && (Sources is null or 0) && (Operations is null or 0)
        && (Cursors is null or 0) && (Buffers is null or 0) && (Owners is null or 0);
}

internal sealed record RecoveryPhase(string Name,long SteadyStartMonotonicNs,long SteadyEndMonotonicNs,
    double EffectiveSeconds,long PreparationAllocatedBytes,long AllocatedBefore,long AllocatedAfter,long AllocatedBytes,
    int Gen0,int Gen1,int Gen2,ProcessReading ProcessBefore,ProcessReading ProcessAfter,
    double UserCpuSeconds,double SystemCpuSeconds,RecoveryOwners BeforeOwners,RecoveryOwners AfterOwners,
    bool QueueFullyDrained,bool ReturnedToWarmOwnerBaseline,long PendingThreadPoolWorkAfterDrain,
    OfferedReceipt OfferedLoad,CaseCount[] Cases);

internal sealed record RecoveryReceipt(int SchemaVersion,string Route,string Provider,bool NativeAot,
    string PackageVersion,string ExecutableSha256,string? LoadedNativePath,string? LoadedNativeSha256,
    string[] ActualCaseIds,string Mode,string SessionMode,int Concurrency,int WarmupPerWorker,int QueueCapacity,
    int RequestedRows,int BatchRows,int Tenants,int OrdersPerTenant,
    double FixedRequestsPerSecond,double FixedSeconds,double OverloadRequestsPerSecond,double OverloadSeconds,
    bool SameSourceInstance,bool SameWorkerAndSessionInstances,bool PoolRebuiltBetweenPhases,
    RecoveryOwners WarmOwners,RecoveryPhase[] Phases,bool ReferenceAccepted,bool SaturationObserved,
    bool RecoveryAccepted,bool AllPhasesReturnedToWarmOwnerBaseline,RecoveryOwners Drained,
    bool TrackedResourcesReturnedToZero,bool Passed,double ReferenceScheduledP99Microseconds,
    double RecoveryScheduledP99Microseconds,double? ScheduledP99Ratio,bool LatencyThresholdApplied,
    IdleReading? Idle,string Scope);

internal static class RecoveryMeasurement
{
    internal static async Task<RecoveryReceipt> Run(Options options,string route,string provider,string version,
        string? nativePath,string? nativeSha,Func<Task<Worker>> createWorker,
        Func<RecoveryOwners> readOwners,Func<ValueTask> disposeSource,Action<Worker,string>? setPhase=null)
    {
        var workers=new Worker[options.Concurrency];
        using var process=Process.GetCurrentProcess();
        try
        {
            for(var i=0;i<workers.Length;i++)workers[i]=await createWorker().ConfigureAwait(false);
            await Task.WhenAll(workers.Select(worker=>worker.Warmup())).ConfigureAwait(false);
            var selected=workers[0].ActiveNames.ToArray();
            var names=selected.Distinct(StringComparer.Ordinal).ToArray();
            var indices=names.Select(name=>Array.IndexOf(Workload.Names,name)).ToArray();
            var operations=workers.Select(worker=>new Func<long,ValueTask<OperationResult>>(worker.ExecuteScheduled)).ToArray();
            var warm=readOwners();
            if(!warm.MatchesQuiescent(warm))throw new InvalidOperationException("Warmup left an active owner.");
            var phases=new RecoveryPhase[3];
            phases[0]=await Phase("reference",options.RecoverySeconds,options.RecoveryRate).ConfigureAwait(false);
            phases[1]=await Phase("overload",options.OverloadSeconds,options.OverloadRate).ConfigureAwait(false);
            phases[2]=await Phase("recovery",options.RecoverySeconds,options.RecoveryRate).ConfigureAwait(false);

            // Worker/source construction happens once. Their persistent sessions,
            // plans, command caches and connection pool survive all three phases.
            for(var i=0;i<workers.Length;i++){await workers[i].DisposeAsync().ConfigureAwait(false);workers[i]=null!;}
            await disposeSource().ConfigureAwait(false);
            var drained=readOwners();
            var ownersZero=drained.AllTrackedZero;
            var reference=Accepted(phases[0]);var saturation=phases[1].OfferedLoad.Rejected>0;
            var recovered=Accepted(phases[2]);var returned=phases.All(phase=>phase.ReturnedToWarmOwnerBaseline);
            var referenceP99=Quantile(phases[0].OfferedLoad.SuccessScheduledHistogram,phases[0].OfferedLoad.Succeeded,.99);
            var recoveryP99=Quantile(phases[2].OfferedLoad.SuccessScheduledHistogram,phases[2].OfferedLoad.Succeeded,.99);
            IdleReading? idle=null;
            if(options.IdleSeconds>0)
            {
                var before=ProcessReading.Read(process);var managedBefore=GC.GetTotalMemory(false);
                var startedUtc=DateTimeOffset.UtcNow.ToString("O");var started=Stopwatch.GetTimestamp();
                await Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds)).ConfigureAwait(false);
                var finished=Stopwatch.GetTimestamp();var finishedUtc=DateTimeOffset.UtcNow.ToString("O");
                var managedAfter=GC.GetTotalMemory(false);var after=ProcessReading.Read(process);
                idle=new(startedUtc,finishedUtc,options.IdleSeconds,(finished-started)/(double)Stopwatch.Frequency,before,after,
                    after.UserCpuSeconds-before.UserCpuSeconds,after.SystemCpuSeconds-before.SystemCpuSeconds,
                    managedBefore,managedAfter,"After all three phases and final source/session disposal; no forced GC; outside phase measurements");
            }
            var path=Environment.ProcessPath??throw new InvalidOperationException("Missing executable identity.");
            string executableSha;using(var file=File.OpenRead(path))executableSha=Convert.ToHexStringLower(SHA256.HashData(file));
            return new(1,route,provider,true,version,executableSha,nativePath,nativeSha,selected,options.Mode,options.SessionMode,
                options.Concurrency,options.Warmup,options.QueueCapacity,options.Rows,options.BatchRows,options.Tenants,options.OrdersPerTenant,
                options.RecoveryRate,options.RecoverySeconds,options.OverloadRate,options.OverloadSeconds,true,true,false,warm,phases,
                reference,saturation,recovered,returned,drained,ownersZero,reference&&saturation&&recovered&&returned&&ownersZero,
                referenceP99,recoveryP99,referenceP99>0?recoveryP99/referenceP99:null,false,idle,
                "Same process/source/pool and persistent worker/session objects across reference, overload, complete drain, recovery. " +
                "Passed verifies accepted sustainable requests, observed admission saturation and tracked resource recovery; " +
                "tail-latency thresholds are not invented here. Raw all-thread allocation totals include business objects and harness; " +
                "phase scheduler preparation is recorded separately, with no framework-only zero claim. Owner visibility is explicit in each snapshot.");

            async Task<RecoveryPhase> Phase(string name,double seconds,double rate)
            {
                if(setPhase is not null)foreach(var worker in workers)setPhase(worker,name);
                var preparationBefore=GC.GetTotalAllocatedBytes(true);
                var beforeCases=CaseSnapshot();
                using var schedule=new OfferedLoad(seconds,rate,options.QueueCapacity,operations);
                var beforeOwners=readOwners();var beforeProcess=ProcessReading.Read(process);
                var before0=GC.CollectionCount(0);var before1=GC.CollectionCount(1);var before2=GC.CollectionCount(2);
                var beforeBytes=GC.GetTotalAllocatedBytes(true);
                schedule.Start();
                await schedule.Completion.ConfigureAwait(false);
                var ended=Stopwatch.GetTimestamp();
                var afterBytes=GC.GetTotalAllocatedBytes(true);
                var after0=GC.CollectionCount(0);var after1=GC.CollectionCount(1);var after2=GC.CollectionCount(2);
                var afterProcess=ProcessReading.Read(process);var afterOwners=readOwners();
                var pending=ThreadPool.PendingWorkItemCount;
                // Snapshot and per-case aggregation allocate only after the raw end.
                var offered=schedule.Snapshot();var afterCases=CaseSnapshot();
                for(var i=0;i<afterCases.Length;i++)
                {
                    var before=beforeCases[i];var after=afterCases[i];
                    afterCases[i]=new(after.Name,after.Started-before.Started,after.Succeeded-before.Succeeded,
                        after.Failed-before.Failed,after.Rows-before.Rows,after.Suspensions-before.Suspensions);
                }
                if(afterCases.Sum(item=>item.Started)!=offered.Started||afterCases.Sum(item=>item.Succeeded)!=offered.Succeeded
                    ||afterCases.Sum(item=>item.Failed)!=offered.Failed||afterCases.Sum(item=>item.Rows)!=offered.Rows)
                    throw new InvalidOperationException("Recovery phase case ledger does not reconcile.");
                var queueDrained=offered.Sent==offered.Started&&offered.Started==offered.Completed
                    &&offered.Completed==offered.Succeeded+offered.Failed;
                return new(name,MonotonicNs(schedule.StartedTicks),MonotonicNs(ended),
                    (ended-schedule.StartedTicks)/(double)Stopwatch.Frequency,beforeBytes-preparationBefore,
                    beforeBytes,afterBytes,afterBytes-beforeBytes,after0-before0,after1-before1,after2-before2,
                    beforeProcess,afterProcess,afterProcess.UserCpuSeconds-beforeProcess.UserCpuSeconds,
                    afterProcess.SystemCpuSeconds-beforeProcess.SystemCpuSeconds,beforeOwners,afterOwners,queueDrained,
                    beforeOwners.MatchesQuiescent(warm)&&afterOwners.MatchesQuiescent(warm),pending,offered,afterCases);
            }

            CaseCount[] CaseSnapshot()
            {
                var result=new CaseCount[names.Length];
                for(var n=0;n<names.Length;n++)
                {
                    long started=0,succeeded=0,failed=0,rows=0,suspensions=0;var index=indices[n];
                    foreach(var worker in workers)
                    {
                        started+=worker.CaseStarted[index];succeeded+=worker.CaseSucceeded[index];failed+=worker.CaseFailed[index];
                        rows+=worker.CaseRows[index];suspensions+=worker.CaseSuspensions[index];
                    }
                    result[n]=new(names[n],started,succeeded,failed,rows,suspensions);
                }
                return result;
            }
        }
        finally
        {
            foreach(var worker in workers)if(worker is not null)await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool Accepted(RecoveryPhase phase)=>phase.OfferedLoad.Rejected==0&&phase.OfferedLoad.Failed==0
        &&phase.OfferedLoad.Succeeded==phase.OfferedLoad.Planned&&phase.QueueFullyDrained;
    private static long MonotonicNs(long ticks)=>checked((long)((Int128)ticks*1_000_000_000/Stopwatch.Frequency));
    private static double Quantile(long[] histogram,long count,double quantile)
    {
        if(count==0)return 0;
        var desired=(long)Math.Ceiling(count*quantile);long seen=0;
        for(var i=0;i<histogram.Length;i++)
        {
            seen+=histogram[i];if(seen<desired)continue;
            var lower=Math.Pow(2,i/128);return(lower+lower*((i%128+1)/128d))/1000;
        }
        throw new InvalidOperationException("Recovery histogram misses successful operations.");
    }
}
