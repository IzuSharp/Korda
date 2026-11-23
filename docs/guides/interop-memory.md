# Immutable native buffers

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

`Korda.Interop.NativeBuffer` is a sealed immutable byte owner in
`Korda.Core`. An application's typed engine returns it; the application
uses `using`, scoped `Read`, explicit `CopyTo`/`ToArray`, or `CopyToAsync`.
See the [compiled API reference](../../src/Interop.Memory/api-reference.md) and
[runnable minimal/advanced consumer](../../samples/api/interop-memory/Program.cs).
Installing or running these managed APIs starts no Rust runtime. A03 supplies the
single admission/physical-lease implementation; A04 does not copy that algorithm.

## Read and copy

`Read` invokes a synchronous delegate on the calling thread outside admission
locks. Its span is valid only during that invocation. Return a scalar or copied
managed value; a ref-like generic result is rejected at compile time. A reader
may call Dispose on the same buffer and continue its already-admitted span. A
reader/mapper exception releases its lease in finally. There is no escaping Span
property, native-backed Memory, public pointer or IMemoryOwner implementation.

`CopyTo(destination)` copies Length bytes and leaves the destination tail intact;
a short destination throws before writing. `CopyTo(offset, destination)` copies
exactly destination.Length bytes after overflow-safe range validation; an empty
range at Length is valid. `TryCopyTo` admits a read before testing capacity, so a
closed owner throws even with a short destination. False means only insufficient
capacity, with no writes. Copy operations use overlap-safe BCL Span.CopyTo.
`ToArray` acquires admission before allocating; the returned CLR array survives
native disposal. Immutable Length remains readable after disposal. IsDisposed
means no new admission, not that earlier readers have physically retired.

## Asynchronous export

`CopyToAsync` validates null destination, CanWrite, then source admission, in that
order, synchronously. Only then is pre-cancellation observed. A canceled token
returns a canceled Task carrying that token, even for an empty buffer, without
copying, staging or writing. An admitted uncanceled empty buffer returns an
already-completed Task and never calls the destination.

Nonempty copies allocate one array of exactly min(Length, 65,536) bytes. Each source
chunk is copied synchronously and the same array is reused only after the previous
WriteAsync completes. One admitted source lease spans all writes. Dispose,
cancellation and a stream's early return cannot revoke an in-flight write's memory;
the lease and array remain alive until that write actually completes. Stream
errors and cancellation can follow partial output and do not undo bytes. The
stream stays open. No span crosses an await, no pool retention is hidden, and no
zero-copy or total-RSS claim is made.

## Engine-maintainer adoption

`NativeBufferFactory.Adopt(engine, owner, address, length)` validates shape and
creates the wrapper. Ownership transfers only on successful return. On failure,
the caller still owns and must dispose its SafeHandle. On success it must never
dispose or adopt that same handle again. The creating type's SafeHandle carries the
exact destructor and independently retains every context the destructor needs.
A02 keeps native modules process-resident; the EngineInfo passed to A04 is not a
substitute for an owner-thread or parent-retention protocol.

The maintainer guarantees immutable initialized, address-stable bytes and applies
the actual domain's negotiated per-result bound before adoption. EngineInfo has
no universal result-size field: Adopt checks the representable Int32 extent, not
an invented generic engine limit. It rejects closed/invalid owners, negative
length, nonempty null data and address-range overflow; it cannot probe an arbitrary
address for accessibility. Thread-affine/asynchronously destroyed or mutable
resources use their own accepted domain profile.

The internal D07 validator separately checks count×width, offset+length,
allocation capacity, finite limit, natural alignment, native-width conversion and
isize::MAX before any native slice/span construction. Zero-width and strided
profiles are not accepted. Empty null+zero data uses empty spans/slices.

## Native ownership and costs

The native conformance module uses actual Rust Vec bytes and Arc backing owners.
A slice has one independent opaque owner and retains the entire actual Vec
capacity charge; releasing the original owner or delivering a one-byte slice does
not return the backing budget. The last backing release does. Fallible reserve
and failed-publication paths leave null outputs and drop unpublished storage.
Byte copying from a managed input is synchronous and ends before that borrow's
pin ends. This is a component fixture, not a new universal Core native allocator.
The fixture's EngineInfo is internal component metadata; it does not re-accept
A02's independently verified bootstrap or prove an assembled engine.

| Operation | Managed allocation/copy | Native/lifetime work |
| --- | --- | --- |
| Read / TryCopyTo / CopyTo | No lease allocation; Read imposes no copy | A03 admission + AddRef/Release; no per-byte FFI |
| ToArray | One Length array (empty may share), one copy | One complete source lease |
| CopyToAsync | One exact min(Length,64 KiB) array plus async Task state, one copy per chunk | One lease until final write completes |
| Adopt | Wrapper plus private gate objects; no byte copy | Transfers one creator SafeHandle on success only |
| Dispose | No user callback; may return before live leases end | Creator destructor runs once after final reference |

## Verification

```sh
python3 tests/Interop.Memory/verify.py
```

The Linux x64 suite executes JIT and real Native AOT test/sample binaries. It
compares managed and Rust arithmetic with an independent arbitrary-precision
BigInteger oracle (seed 403004); checks exact/short/range/overlap copies,
malformed descriptors, retained slices and full backing charges, failed reserve
and publication, mapper cleanup, deterministic read/Dispose/GC interleavings,
cancel/Dispose while WriteAsync retains staging, partial write failures and
creator finalizer counters. A compiled-symbol gate checks every exact pinned API
signature, generic/nullability/ownership negative consumers, XML and generated
reference. Raw logs and source/native/executable hashes live under
`artifacts/task03/A04/`; Windows and A16/B24's pristine package composition remain
separate obligations.
