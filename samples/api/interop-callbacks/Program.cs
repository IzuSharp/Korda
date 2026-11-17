using NativeWeave.Interop.Callbacks;

// Maintainer component example. Applications use a domain's typed managed
// subscription API; they never supply the private cookie or unmanaged callback.
using var coordinator = new CallbackCoordinator(capacity: 2);
var observed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
var subscription = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator,
    maxItems: 4, maxBytes: 4096, maxPayload: 1024,
    handler: (_, bytes) => { observed.TrySetResult(bytes.ToArray()); return ValueTask.CompletedTask; });
subscription.Start();
if (Fixture.Emit(subscription, [1, 2, 3]) != 0) throw new Exception("Notification rejected.");
byte[] value = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
await subscription.DisposeAsync();
await subscription.Completion;
if (!value.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }) || subscription.Snapshot.RetainedBytes != 0)
    throw new Exception("Copied notification or final drain failed.");

// Advanced: already admitted asynchronous handlers keep their payload/context
// alive while an explicit stop discards queued ordinary notifications.
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var advanced = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 2, 64, 32,
    handler: async (kind, bytes) => { started.SetResult(); await finished.Task; _ = bytes.Length; });
advanced.Start();
if (Fixture.Emit(advanced, [7]) != 0) throw new Exception("Notification rejected.");
await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
advanced.Dispose();
if (advanced.Retirement.IsCompleted) throw new Exception("An active handler was reclaimed.");
finished.SetResult();
await advanced.DisposeAsync();
await advanced.Completion;
Console.WriteLine("A06 maintainer example: copied notification and asynchronous handler drain passed.");
