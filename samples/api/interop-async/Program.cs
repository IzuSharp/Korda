using Korda.Interop;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;

// Maintainer example: the application-facing domain method creates an exact
// native operation and returns this Task<NativeBuffer>. No cookie/ABI is public.
using var coordinator = new CallbackCoordinator(capacity: 2);
var operation = new AsyncOperation<NativeBuffer>(Fixture.New(mode: 2), new FixtureCalls(), coordinator, Fixture.TakeBuffer);
using (NativeBuffer result = await operation.Start())
{
    await operation.DisposeAsync();
    if (!result.Read(static bytes => bytes.SequenceEqual(new byte[] { 1, 2, 3 })))
        throw new Exception("The independently owned result did not survive operation retirement.");
}

// Advanced: cancellation requests native cooperation. The accepted native
// operation can still commit success, and its source/input obligations can
// outlive the public Task. A canceled close wait does not release those owners.
using var cancel = new CancellationTokenSource();
var advanced = new AsyncOperation<NativeBuffer>(Fixture.New(), new FixtureCalls(), coordinator, Fixture.TakeBuffer, cancel.Token);
Task<NativeBuffer> pending = advanced.Start();
using SourceHandle source = Fixture.HoldSource(advanced);
cancel.Cancel();
if (pending.IsCompleted) throw new Exception("Requesting cancellation selected a premature outcome.");
if (Fixture.Complete(advanced) != 0) throw new Exception("Native success did not commit.");
using NativeBuffer owned = await pending;
if (advanced.Retirement.IsCompleted) throw new Exception("A native source was reclaimed before it exited.");
using (var waiter = new CancellationTokenSource())
{
    waiter.Cancel();
    try { await advanced.DisposeAsync().AsTask().WaitAsync(waiter.Token); }
    catch (OperationCanceledException) { }
}
source.Dispose();
await advanced.DisposeAsync();
if (Fixture.Count(0) != 0 || !owned.Read(static bytes => bytes.Length == 3))
    throw new Exception("Cleanup/result ownership failed.");
Console.WriteLine("A07 maintainer example: synchronous completion, typed result ownership, late cancellation and independent native retirement passed.");
