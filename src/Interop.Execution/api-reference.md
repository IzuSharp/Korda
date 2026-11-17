# Internal executor close reference

Generated from the compiled NativeWeave.Core assembly and XML by `tests/Interop.Execution/ApiCheck`.

A08 adds **zero public types or members**. Two namespace types remain internal: the sealed `ExecutorLifetime` and readonly `ExecutorJoinResult`.

**Implementation summary:** Engine-owned close observation whose canceled waits preserve actual cleanup.

`RequestClose()` is nonblocking and idempotent. `CloseAsync(CancellationToken waitCancellation = default)` returns `Task<ExecutorJoinResult>`; its token cancels only the caller's wait. `DisposeAsync()` returns `ValueTask` and completes after typed worker-join evidence and A07 retirement. The constructor takes the exact typed A07 operation and a trusted owned-callback scope query. An owned callback may request close and is rejected if it attempts to await its own drain.

The immutable result records Joined, WorkersStarted, WorkersStopped, OutstandingOperations and ReservedBytes. Completion requires actual join, equal positive started/stopped counts and zero operation/byte ownership. An unrelated consumer is rejected with CS0122. No public scheduler or default runtime is introduced.
