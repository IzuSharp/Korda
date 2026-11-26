using System.Runtime.CompilerServices;
using System.Text.Json;

if(RuntimeFeature.IsDynamicCodeSupported)throw new InvalidOperationException("Native AOT execution is required.");
var options=Options.Parse(args);
await using var source=new DatabaseDriver(options);
await source.VerifyDataset(options);
if(options.LifecycleChecks)
{
    var lifecycle=await LifecycleVerification.Run(source,options);
    var lifecycleJson=JsonSerializer.Serialize(lifecycle,PerfJson.Default.LifecycleReceipt);
    if(options.Output is not null)File.WriteAllText(options.Output,lifecycleJson+"\n");
    Console.WriteLine("DB_PERF_LIFECYCLE "+lifecycleJson);
    return lifecycle.Checks.Any(check=>check.Status is "failed" or "not-proven")?1:0;
}
var result=await Measurement.Run(source,options);
var json=JsonSerializer.Serialize(result,PerfJson.Default.RunReceipt);
if(options.Output is not null)File.WriteAllText(options.Output,json+"\n");
Console.WriteLine("DB_PERF_RESULT "+json);
return result.Failed==0&&result.ApplicationConnectionsReturnedToZero?0:1;
