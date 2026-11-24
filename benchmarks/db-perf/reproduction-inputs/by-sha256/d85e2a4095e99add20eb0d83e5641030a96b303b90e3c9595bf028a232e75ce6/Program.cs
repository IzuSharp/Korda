using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
if(RuntimeFeature.IsDynamicCodeSupported)throw new InvalidOperationException("Native AOT required");
if(args.Length!=2)throw new ArgumentException("disabled|enabled output.json");
if(args[0]=="disabled")
{
 for(var i=0;i<10000;i++)if(NativeAllocationProfile.Create(false)is not null)throw new InvalidOperationException("Disabled profiler exists");
 _=GC.GetTotalAllocatedBytes(true);GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
 var before=GC.GetTotalAllocatedBytes(true);
 for(var i=0;i<100000;i++)if(NativeAllocationProfile.Create(false)is not null)throw new InvalidOperationException("Disabled profiler exists");
 var after=GC.GetTotalAllocatedBytes(true);
 var receipt=new DisabledReceipt(true,100000,before,after,after-before,after==before);
 var json=JsonSerializer.Serialize(receipt,TestJson.Default.DisabledReceipt);File.WriteAllText(args[1],json+"\n");Console.WriteLine(json);
 return receipt.Passed?0:1;
}
try
{
 using var profile=NativeAllocationProfile.Create(true)??throw new InvalidOperationException("Profiler missing");
 var module=NativeLibrary.Load(Environment.GetEnvironmentVariable("DB_PERF_ALLOC_PROFILE_LIBRARY")!);
 try
 {
  var malloc=Marshal.GetDelegateForFunctionPointer<Malloc>(NativeLibrary.GetExport(module,"malloc"));
  var free=Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(module,"free"));
  var owned=new nint[4096];
  var warm=malloc(64);free(warm);
  profile.CaptureBefore();
  for(var i=0;i<owned.Length;i++){owned[i]=malloc(64);if(owned[i]==0)throw new OutOfMemoryException();}
  profile.CaptureAfter();
  for(var i=0;i<owned.Length;i++)free(owned[i]);
  profile.CaptureAfterDrain();
  var receipt=profile.CreateReceipt();
  var json=JsonSerializer.Serialize(receipt,TestJson.Default.NativeAllocationProfileReceipt);File.WriteAllText(args[1],json+"\n");Console.WriteLine(json);
  return receipt.Passed?0:1;
 }
 finally{NativeLibrary.Free(module);}
}
catch(Exception error)
{
 var receipt=new ErrorReceipt(error.GetType().FullName!,error.Message,false);
 var json=JsonSerializer.Serialize(receipt,TestJson.Default.ErrorReceipt);File.WriteAllText(args[1],json+"\n");Console.WriteLine(json);return 1;
}
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate nint Malloc(nuint bytes);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]internal delegate void Free(nint pointer);
internal sealed record DisabledReceipt(bool NativeAot,int Calls,long AllocatedBefore,long AllocatedAfter,long AllocatedBytes,bool Passed);
internal sealed record ErrorReceipt(string Type,string Message,bool Passed);
[JsonSerializable(typeof(NativeAllocationProfileReceipt))]
[JsonSerializable(typeof(DisabledReceipt))]
[JsonSerializable(typeof(ErrorReceipt))]
internal partial class TestJson:JsonSerializerContext{}
