# Bounded Server bodies and owned chunks

B16 contributes the retained-byte coordinator, native `http_body::Body` bridge,
bounded native reader, `BodyChunk`, and asynchronous borrowed Stream adapters.
These sources compose into `NativeWeave.Server`; they do not create another
runtime, listener, module loader, callback ABI or independently published package.
B15 owns the request/response/result APIs and B17 supplies their real Rust Host.

## Choose one input mode

The first actual input operation selects Stream, `ReadChunkAsync`, or static
`ReadJsonAsync`. Merely obtaining `Request.Body` does not choose a mode. One read
may be pending; mixing modes or concurrent reads fails explicitly. Protocol
capability checks precede body-mode selection. Borrowed streams support async
Memory/ValueTask operations and are nonseekable; synchronous I/O is unsupported.

An owned `BodyChunk` has the exact public API:

```csharp
public sealed class BodyChunk : IDisposable
{
    public int Length { get; }
    public void CopyTo(Span<byte> destination);
    public byte[] ToArray();
    public void Dispose();
}
```

Dispose each returned chunk. Null from a read means **EndObserved**, which does
not establish peer consumption or a precise HTTP/2 reset cause. B15 exposes
trailers only after that observation. Empty data chunks are never returned.

```csharp
using BodyChunk chunk = await ReadNextChunkAsync(cancellationToken);
byte[] applicationOwned = chunk.ToArray();
```

`ReadNextChunkAsync` above denotes the application's typed input source. The
maintained minimal and advanced consumers are in
[`samples/api/server-streaming`](../../samples/api/server-streaming/README.md).
They are compiled and executed by the B16 component checks; Host application
consumption is verified by B15/B17.

## Allocation ownership and costs

`CopyTo` writes into caller-owned memory, needs no extra framework payload
allocation, and leaves an oversized destination's tail unchanged. A short
destination is rejected without a partial copy. `ToArray` first obtains a
nonwaiting managed grant, allocates one array, copies, and releases its temporary
credit as ownership transfers to the caller. Arbitrary caller retention of that
array is outside the framework ledger. Both operations are O(visible bytes).

Core `NativeResourceOwner` supplies logical admission and physical SafeHandle
leases. Concurrent disposal prevents new reads while an admitted copy continues
safely. `Length`, `CopyTo`, and `ToArray` after disposal throw
`ObjectDisposedException`. Dispose is nonblocking and idempotent; the creating
module's bounded destructor performs final reclamation.

Native `Bytes::from_owner` carries the exact requested Box backing extent and
its unique credit. Every nonempty clone or slice retains that full allocation's
charge. A one-byte slice of a 32 KiB backing still consumes 32 KiB. The native
reader may split one already charged allocation into several visible chunks;
it does not silently release the backing when a queue slot or original view ends.

The transport's parser/TLS/socket buffers, allocator bookkeeping, application
DTOs, caller-provided memory, and the whole process are not falsely represented
by the controlled payload counters. B17 must separately bound its input frames;
`BodyReader` rejects a source data frame larger than its negotiated chunk bound.

## Nonwaiting payload admission

Native and managed payload pools are independent. Each pool applies both an
engine-global cap and a per-request cap under one small bookkeeping gate. A
grant either increments both quotas or changes neither. No callback, payload
allocation, byte waiter, sleep, retry loop or user code runs under that gate.

The ReferenceSmall profile uses 4 MiB per request per side and 32 KiB chunks.
The supplied global defaults are 64 MiB per side. The coordinator accepts the
validated configured limits; it does not create global process state.

A writer may await a bounded queue slot with fixed metadata only. After that
slot is obtained, it tries the native grant once. Insufficient capacity returns
`resource_limit` immediately and releases the slot. An additional managed
encoding/copy grant is also nonwaiting; failure releases the native grant and
slot. An accepted native copy owns the credit through queueing, Body polling,
and every retained `Bytes` reference.

128 retained 32 KiB input owners exhaust a 4 MiB request cap. A new positive
read or output write then fails promptly, even if only that handler's finally
can release the input owners. A caller may deliberately release owners and
submit another operation; the framework does not hide retries. Payload-free
finish/abort/control uses separately bounded metadata.

## Borrowed streams and cancellation

`ServerReadStream` limits each read to the smaller of caller destination and
configured chunk size. The native owner is disposed after the synchronous copy.
Its `CopyToAsync` override reserves one bounded managed staging array before
allocation, keeps at most one chunk live, and returns both owners on destination
failure or cancellation. It leaves the destination open.

`ServerWriteStream` delegates into B15's response-start pipeline and rejects
overlapping writes/flushes. Its native transport bounds larger caller memory
into admitted chunks without allowing a borrowed pointer to escape an FFI call.
Disposing either facade closes that body's logical admission; it does not
finish/dispose the request, server or module. An already admitted operation
retains its own lifetime. Retaining an owned chunk does not retain access to
borrowed streams, request metadata or scoped services.

Cancellation is intent, not native retirement. Core create/start/take/retire
still owns operation callbacks, cancellation registrations and unused results.
Dropping an unstarted operation, an untaken result or a failed wrapper releases
its remaining payload owners. A successfully taken chunk remains valid after
operation and request dispatch retirement, using its independent request credit
anchor for a later bounded `ToArray` copy.

## Native mapping and module composition

| Surface | Adapter responsibility | Host's exact native operation |
| --- | --- | --- |
| Owned chunk | Core admission, bounded copy, creator destructor | `nw_server_buffer_length`, `nw_server_buffer_copy`, `nw_server_buffer_release` |
| Chunk array copy | Temporary managed overlap grant from retained chunk anchor | `nw_server_buffer_managed_credit_acquire`, `nw_server_managed_credit_release` |
| Borrowed input | Single mode/read, positive bounded chunk or EndObserved | `nw_server_body_read_create`, `nw_server_operation_take_chunk` |
| Borrowed output | B15 response-start pipeline, bounded write/flush | `nw_server_write_reserve_create`, `nw_server_response_write_create`, `nw_server_response_flush_create` |
| Finish | One shared completion attempt, separate byte retirement | `nw_server_response_finish_create` |

The default Rust rlib exports no native fixture symbols. Its optional
`conformance` feature exposes only a test fixture over the same production
allocation/credit objects and the existing Core Operation. It is not another
supported Server ABI and is never a substitute for B17's negotiated Host.

## Verification and applicability

```sh
python3 tests/Server.Streaming/verify.py --offline
```

The script captures commands, toolchain identities, source hashes, native tests,
Clippy/format results, JIT and actual Linux x64 Native AOT execution, sample
execution, compiled API/XML/reference checks and artifact hashes. Offline mode
reuses pinned package caches and records that vulnerability information was not
refreshed. It does not weaken compilation, API checks or native tests.

Native cases cover global/request atomic grants, retained slicing, owned bytes
after close, queue cancellation without payload, old permits across close,
Body EOF separate from last-owner drop, 128 retained chunks, copy overlap
rollback, input Body slicing, pending network-read cancellation and Core
operation result retirement. Managed cases cover real native buffers and
Core async operations, concurrent copy/dispose, disposed access, short-copy
atomicity, array-transfer budget, pre-cancel, mapping/destination failures,
read-mode and concurrent-I/O rejection, borrowed closure and compiled consumers.

This is Linux x64 component acceptance. It does not claim Windows execution,
HTTP/TLS Host acceptance, an RSS bound, throughput superiority or full B22/B23
load acceptance. B15/B17 own their assembled HTTP requirements; A16/B24 own
final pristine distribution. Independent technical review is recorded separately
from the implementation author's actual execution results.
