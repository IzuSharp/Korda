# Server streaming API reference

Generated from compiled BodyChunk metadata and XML. The public declaration is owned by B16 and composed into NativeWeave.Server; all transport/credit/stream adapter types remain internal.

## `M:NativeWeave.Server.BodyChunk.CopyTo(System.Span{System.Byte})`

Copies this complete chunk into the start of caller-owned memory.

O(Length), one bounded copy, no new framework payload allocation. The caller controls the destination's retention. Native ownership remains valid for the entire synchronous call.

## `M:NativeWeave.Server.BodyChunk.Dispose`

Closes new copy admission and releases the creating module's owner exactly once.

Thread-safe, nonblocking and idempotent. An admitted copy keeps the owner alive until its finally path releases the Core lease. This does not dispose the parent request or server.

## `M:NativeWeave.Server.BodyChunk.ToArray`

Copies the chunk into a new, independently owned managed array.

Acquires managed request/global credit without waiting before allocating. Native and managed copies are both charged during overlap, including after dispatch has ended. The credit is released when the array transfers to the caller; arbitrary caller retention is outside the framework ledger. O(Length), one array and one copy.

## `P:NativeWeave.Server.BodyChunk.Length`

Gets the positive visible byte length while this chunk remains open.

O(1), no payload allocation. A slice can keep a larger backing allocation charged.

## `T:NativeWeave.Server.BodyChunk`

Owns one immutable native request-body chunk independently of its request context.

Dispose every chunk. The creating native module retains its full backing allocation and request/global credit until the last native owner is released. Copies use Core logical admission and SafeHandle leases; a concurrent Dispose closes new copies without invalidating an admitted one. No pointer, borrowed span or native-backed Memory escapes. Chunk ownership may outlive dispatch; it does not extend access to request services, borrowed streams or metadata.

