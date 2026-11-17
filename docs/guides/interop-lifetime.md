# Synchronous ownership and retirement

A03 implements the private resource admission used by generated bindings and A04
buffers in `NativeWeave.Core`. Application code continues to use its domain's
sealed resource owner with `using`; no raw pointer or independent lifetime package
is part of this task's public surface. See the [compiled internal reference](../../src/Interop.Lifetime/api-reference.md)
and [maintainer consumers](../../samples/api/interop-lifetime/Program.cs).

## Call and close behavior

One short managed gate protects the Open check, logical active count and
`SafeHandle.DangerousAddRef`. Native work and managed mapping execute outside the
gate. Generated adapters use a caller-local acquisition flag and release it in a
`finally`; a later acquisition or mapper exception unwinds all earlier leases.
The synchronous path allocates no lease object, callback context or native runtime.

`Dispose` linearizes Open to Closing and drops the original SafeHandle reference
outside the gate. It is idempotent and does not wait for earlier calls. An admitted
call may enter native code after Closing; later admissions throw
`ObjectDisposedException`. `Retirement` and successful internal `DisposeAsync`
complete only after the owner's Dispose and every final DangerousRelease return.
The retirement Task is lazy and uses asynchronous continuations. It is not a
native async-operation/callback retirement coordinator; A06/A07 own that protocol.

Construction is failure atomic: a failed `NativeResourceOwner` construction leaves
the supplied SafeHandle with its caller. A generated wrapper that already received
a native owner must dispose its unpublished SafeHandle on construction failure.
The creator's destructor remains attached to the type-specific private SafeHandle.
It must be nonblocking, nonthrowing and safe on any release thread. There is no
additional finalizer and no user callback from finalization. The actual SafeHandle
must itself retain every managed/native object its destructor needs. The optional
internal lifetimeRoot only protects explicit call/release paths; it does not promise
finalizer ordering after the ordinary wrapper becomes unreachable and cannot serve
as a managed parent-retention mechanism. Native modules remain
process-resident under the existing A02 contract.

## Parent and child

Each child chooses one authoritative parent-retention mechanism. The native test
module gives each child one `Arc<ParentState>`. Closing the parent's managed wrapper
blocks new parent operations and child creation, while an already-created child
keeps its state and remains usable. A failed child creation drops its temporary
native retain. Managed and native parent retention are not layered into a cycle.
The fixture is a real Rust allocation/release component, not a database or server.

## Verification and cost

Run on Linux x64 with the pinned SDK/Rust toolchain and native linker:

```sh
python3 tests/Interop.Lifetime/verify.py
```

The script records tool versions, exact sources/lock files, native/executable
hashes, stdout/stderr and results beneath `artifacts/task03/A03/`. It executes real
JIT and Native AOT consumers, actual Rust release counters, controlled acquisition
boundaries, rollback after injected allocation/mapping errors, parent/child
retention, repeated disposal, finalizer fallback and GC with an admitted call.
Per schedule there are at most three participating threads, two calls and eight
forced phase switches; these finite schedules do not prove all possible OS
interleavings. Test-only boundary hooks are null on production adapters.

A synchronous call pays a short admission critical section, SafeHandle AddRef and
Release and its actual FFI call. The component probe measures managed allocation
for 1,024 warmed synchronous calls. It makes no total-process memory or throughput
claim. The generated API check proves the compiled component introduces no public
ownership plumbing. Linux success does not accept Windows; A16/B24 retain the full
pristine package-consumption obligations.
