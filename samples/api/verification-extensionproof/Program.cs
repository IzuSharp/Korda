using System.Runtime.CompilerServices;
using NativeWeave.Verification.ExtensionProof;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new Exception("Actual Native AOT required.");
// Internal maintainer example. This thread owns progress; no device/window API is exposed.
using var owner = new AffinityProbe(capacity: 2);
owner.Start();
using var command = new OwnedWork(42);
if (owner.TryPost(command) != 0 || owner.Pump(1) != 0 || owner.Read(out ulong value) != 0 || value != 42)
    throw new Exception("Owner-thread command failed.");
owner.Dispose();
long deadline = Environment.TickCount64 + 10000;
while (!owner.CallbacksRetired)
{
    if (Environment.TickCount64 > deadline) throw new Exception("Callback drain did not finish.");
    Thread.Sleep(1);
}
if (!owner.ClosePending || owner.Pump() != 0 || !owner.TryFinishClose()) throw new Exception("Explicit owner retirement failed.");
Console.WriteLine("PASS internal Linux owner pump, success-only command transfer and separate callback/affine retirement.");
