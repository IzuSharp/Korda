using System.Runtime.CompilerServices;
using System.Text.Json;

if(RuntimeFeature.IsDynamicCodeSupported)throw new InvalidOperationException("Native AOT execution is required.");
var options=Options.Parse(args);
try
{
await using var source=new DatabaseDriver(options);
await source.VerifyDataset(options);
if(options.RecoveryChecks)
{
    RecoveryOwners ReadOwners()=>new(null,source.Read().ApplicationConnections,null,null,null,null,null,null,
        "Application-owned logical connections only; internal provider native/reader ownership is unobservable");
    var version="Dapper.AOT/1.1.0;Dapper/2.1.100;"+source.Provider+"/11.0.0";
    var recovery=await RecoveryMeasurement.Run(options,"dapper-aot-recovery",source.Provider,version,
        source.NativePath,source.NativeHash,()=>Worker.Create(source,options),ReadOwners,source.DisposeAsync);
    var recoveryJson=JsonSerializer.Serialize(recovery,PerfJson.Default.RecoveryReceipt);
    if(options.Output is not null)File.WriteAllText(options.Output,recoveryJson+"\n");
    Console.WriteLine("DB_PERF_RECOVERY "+recoveryJson);
    return recovery.Passed && CorrectnessOracle.Failures == 0 ? 0 : 1;
}
if(options.LifecycleChecks)
{
    CorrectnessOracle.SetPhase("lifecycle-diagnostic");
    var lifecycle=await LifecycleVerification.Run(source,options);
    lifecycle = lifecycle with { OracleFailures = CorrectnessOracle.Failures, FirstOracleFailure = CorrectnessOracle.FirstFailure };
    var lifecycleJson=JsonSerializer.Serialize(lifecycle,PerfJson.Default.LifecycleReceipt);
    if(options.Output is not null)File.WriteAllText(options.Output,lifecycleJson+"\n");
    Console.WriteLine("DB_PERF_LIFECYCLE "+lifecycleJson);
    return lifecycle.Checks.Any(check=>check.Status is "failed" or "not-proven") || CorrectnessOracle.Failures != 0 ? 1 : 0;
}
var result=await Measurement.Run(source,options);
var json=JsonSerializer.Serialize(result,PerfJson.Default.RunReceipt);
if(options.Output is not null)File.WriteAllText(options.Output,json+"\n");
Console.WriteLine("DB_PERF_RESULT "+json);
return result.Failed==0&&result.OracleFailures==0&&result.ApplicationConnectionsReturnedToZero&&result.NativeAllocationProfile?.Passed!=false?0:1;
}
finally { CorrectnessOracle.WriteFailure(options.Output); }
