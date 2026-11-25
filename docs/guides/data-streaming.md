# Bounded data batches and typed streaming

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

This B06 component implements owned native batches and the internal typed stream
adapter on Linux x64. It composes the actual B02 PostgreSQL and B03 SQLite session
adapters and B07 checked scalar codecs. B01 remains the sole owner of projections,
row readers, scalar declarations, errors, and the `db-wire/1` representation.

The exact design input is Korda-Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, B06 revision 0.4.0. The input manifest
in [the tests](../../tests/Data.Streaming/design-inputs.json) records the packet,
fragment, API/declaration and contract hashes. Earlier design executions are
specification inputs, not evidence that this product component executed.

## Component and public API boundaries

`Korda.Data.Streaming` is an internal, non-packable implementation assembly.
Its [generated reference](../../src/Data.Streaming/api-reference.md) must continue
to contain zero exported types. It does not add public native handles, transport
interfaces, another `DataSession`, a managed database provider, or a default Rust
runtime. The canonical B01 public API is unchanged.

| Designed application operation | B06 implementation | Composition owner |
| --- | --- | --- |
| `DataSession.StreamAsync<T>` / `QuerySelection<T>.AsAsyncEnumerable` | Internal `TypedStream<T>`, one-shot enumeration and batch leases | B04/B13 supply the snapshotted command, host operation and public entry point |
| `QueryAsync<T>` / `ToListAsync` / `ToArrayAsync` | Bounded buffered terminals over the same cursor | B04/B13 bind the designed public methods |
| `FirstAsync` / `FirstOrDefaultAsync` | At most one business row, followed by cursor cleanup | The native factory receives a one-row fetch limit |
| `SingleAsync` / `SingleOrDefaultAsync` / `ScalarAsync<T>` | At most two business rows; required cardinality rejects zero/excess rows | Scalar uses B01's exact one-column empty-alias sentinel |
| `Projection<T>` / `RowReader` | Existing B01 declarations and synchronous materializer under a B06 lease | B12 owns additional DTO construction machinery |

The internal terminals preserve the declared return types and generic default
semantics: `OrDefault` means `default(T)`, so absent `long` is zero rather than a
new `Nullable<long>`. A static materializer uses B01 `RowReader.Read`/typed tokens;
it does not receive `object[]` or a reflection mapper. Ordinary DTO changes are C#
changes; there is no model-specific Rust compilation.

The [runnable component sample](../../samples/api/data-streaming/README.md) is
explicitly a maintainer fixture. Its test-only C ABI bridge executes the actual
providers and stream implementation. It is not an installable B04 host, released
operation table, or pristine NuGet consumer. B04/B21/B24 own the assembled host;
A16/B24 own clean package consumption. Windows remains a separate platform row.

## Native composition and dispatch

The caller supplies `Shape`, `Limits`, a finite engine-local `Budget`, an existing
Tokio `Handle`, and an asynchronous source factory to `Cursor::open`. The cursor
checks the canonical projection and reserves its copy-path capacity **before
invoking that factory**. This crate never creates a runtime, connection, pool,
SQL parser, or execution task for a query.

`Source<PgRow>` or `Source<SqliteRow>` contains actual provider rows, observed
statement metadata, a nonblocking stop closure, and a separate provider join
future. The fixture adapters use the provider's own session `describe` and
`stream` methods. The providers keep their SQLx future and physical session lease
inside their existing actor. A SQLx cursor is never made self-referential by
transmuting a connection lifetime.

Statement metadata is checked even when there are no rows. The complete count,
ordinal sequence, exact aliases and database types are checked before the first
nullable scalar decode. B01 supplies ordered descriptor IDs, the projection hash,
source-bound configured codec IDs and semantic profile. A missing nullable column
therefore fails shape validation; it never becomes SQL NULL. SQLite computed
NULL/unknown metadata has no observable declared storage type: that explicit
case uses the admitted projection, and every non-NULL row still has its actual
storage class checked. PostgreSQL and SQLite profile-specific codecs are checked
before dispatch; configured native IDs cannot alias different configurations.

## Row and byte bounds

The native default is 256 rows and 1 MiB of serialized bytes per batch. The
ordinary B01 wire ceiling is 4 MiB. The inline field limit is finite and also
constrained by the complete single-row representation; a larger configured field
limit cannot override the batch limit. Managed buffering defaults to 10,000 rows
and 16 MiB of charged serialized data. These are policy defaults, not throughput
or capacity measurements.

The batch check includes the 96-byte header, every 16-byte descriptor, every
24-byte cell, and all scalar payload bytes. All additions and products are
checked. No row is appended after it would exceed the budget. A bounded decoded
lookahead row can be retained when the next row fits a fresh batch but does not
fit the current one; that row is included in the reservation and no further row
is pulled. Reaching the row limit needs no lookahead. There is no additional
prefetch task or queue in B06, and the path never calls unbounded `fetch_all`.

A single row or field that cannot fit returns `LargeValueRequiresStreaming`
(`DatabaseErrorKind.ResourceLimit` and the corresponding provider code in the
component fixture). Version 1 does not implement incremental single-field BLOB
streaming and does not silently buffer the oversized value as a fallback.
SQLx can already have allocated the provider row before the adapter sees it;
this rejection does not promise a preallocation bound on PostgreSQL packets or
SQLite's internal value storage.

The engine's `Budget` has finite cursor and byte capacity with immediate rejection
and no hidden waiting producers. Each cursor reserves five batch-size credits
plus its bounded row-vector and metadata storage. Those conservative credits
cover simultaneous decoded rows, lookahead, reference tables, encoded output and
conversion scratch. One live batch is allowed per cursor, including a batch
already transferred to its consumer. Another fetch rejects until that owner is
released. An independent batch holds the reservation after cursor closure and
can still be read; releasing the cursor does not return its held-result credit.

Buffered terminals count headers and descriptors as well as row cell/payload
bytes. They check the next row before invoking its materializer and do not switch
to streaming when a list would exceed its limit. Arbitrary application constructor
allocations are outside this charged-data bound.

## Ownership, cancellation and cleanup

1. Stream construction validates the projection and captures a factory over the
   already-snapshotted command; it performs no execution admission. The first
   `MoveNextAsync` starts that factory. Repeated enumeration rejects.
2. At most one `MoveNextAsync`/native fetch is active. Concurrent use rejects
   rather than queuing behind an unknown connection order.
3. A completed native batch is transferred once. Repeated take returns an invalid
   state and no second owner. Managed disposal is idempotent; a native destructor
   is never deliberately called twice with the same pointer.
4. The production host retains a bounded immutable native batch. B01 validates
   its borrowed wire without a managed payload copy; validation, row sizing and
   the complete synchronous materializer hold a temporary SafeHandle reference.
   A managed transport can instead transfer an exclusive array, or use the
   defensive-copy fallback. `RowReader` and spans never cross an await. CLR
   strings/arrays/DTOs can outlive the lease because they own their data.
5. Break, cancellation, failed decoding, or a constructor exception releases
   the current batch, closes further admission, requests stop, and awaits the
   provider's explicit retirement receipt. A constructor exception is preserved;
   an additional cleanup failure is retained separately on the exception under
   `Korda.Database.StreamCleanup.v1`.

The private suspended move uses the runtime's pooling ValueTask builder. Each
returned `ValueTask<bool>` is consumed once; callers needing a reusable Task can
call `AsTask` once and retain that Task. A completed but unconsumed move keeps its
own state until consumption, so a later move cannot overwrite its result.
Concurrent disposal still joins the stream's independent cleanup notification.

The runtime cache has one thread-local slot per participating live thread and
one shared slot per processor for each closed state-machine type. First use and
cache misses may allocate. This removes a repeated move-state allocation from
the measured warm path; it does not establish zero allocation for query creation,
batch fetch, cancellation, thread growth or the complete stream lifetime.

The method and enumerator cancellation tokens are linked when necessary. An
already canceled token does not start the factory. After admission, cancellation
only calls nonblocking native stop control; it does not locally complete the
accepted fetch. The native terminal decides the result. Provider-confirmed
stopped EOF differs from natural EOF, and a late stop request does not rewrite a
previously observed natural end. Cancellation registration is drained outside its
callback before managed cleanup completes.

The host retains an accepted native `fetch` future until its terminal result;
business cancellation requests provider stop instead of dropping that future.
If a caller nevertheless abandons a partially consumed fetch, its local staged
rows can be discarded. The cursor records that abandonment and rejects every
subsequent fetch. Only close/drop cleanup remains valid; there is no claim of
restoring or resuming the old row position.

The native provider guard keeps its join future and cursor reservation when a
close wait is abandoned. Dropping the cursor transfers that *same* pending join
to the supplied existing runtime. There is at most one such cleanup job per
admitted cursor. Dropping the business row stream is not a retirement receipt.
If the runtime is aborted/shut down or the provider cannot confirm safety, the
bounded reservation is deliberately retained for isolation; the code reports
`CleanupPending` and does not fabricate a reusable connection or replenish
admission. The host must keep its runtime alive through admitted cleanup.

Provider-specific proofs remain separate. PostgreSQL requires protocol drain or
confirmed physical retirement. SQLite requires its operation barrier and removal
of the progress handler; session close observes SQLx's worker shutdown
acknowledgement. The public SQLx seam does not expose an OS-thread `JoinHandle`,
so the evidence explicitly reports `os_thread_joined = false` rather than
claiming a thread join.

## Memory and call costs

| Category | Accounting and limitation |
| --- | --- |
| B06 native copy path | Finite conservative reservations plus exact current/peak serialized batch bytes and live owner counts |
| Provider rows | B02/B03 separately report channel plus delivery/producer handoff retention; both tested adapters bound that observed category to two rows with a one-item channel |
| Production managed host | Retains the immutable native batch and protects complete synchronous reads; no managed payload copy |
| B01 validation | Trusted native borrowing and exclusive managed-array handoff avoid copying; ordinary input keeps a defensive copy |
| Maintainer-fixture transport | Its explicit native-to-managed copy and ordinary-input defensive copy remain fixture costs; they do not describe the production host |
| Materialized values | Direct static constructors; strings and byte arrays are owned copies; buffered results retain their DTO/list/array allocations |
| Control calls | The test bridge records fetch/take/copy/stop/join/release; its blocking worker adaptation is harness overhead, not a claimed production callback-host cost |
| Whole process | SQLx receive buffers/caches, SQLite allocations, Tokio stacks, allocator bookkeeping/retention and GC are measured separately; no hard RSS or performance superiority claim |

Pinned SQLx PostgreSQL execution sends protocol `Execute` with **limit 0**. B06's
batch size therefore does not limit server output or all protocol buffering.
The provider-owned row counter includes a possible delivery handoff as channel
capacity becomes available. A channel of capacity one alone is not a proof of
one retained row. Sustained RSS, workload comparisons and long-running limits
remain B22/B23 gates.

## Verification

Use the repository-pinned .NET 11.0.100 / C# 15 and LLVM 23.1.5 toolchains:

```sh
python3 tests/Data.Streaming/verify.py \
  --postgres-bin /path/to/postgresql-18/bin \
  --output artifacts/B06/run-linux
```

The runner starts an isolated PostgreSQL instance in the same process namespace,
builds the actual provider-composed fixture, runs normal managed and explicit
`linux-x64` NativeAOT executables, and stops the service in `finally`. An explicit
`KORDA_TEST_POSTGRES_URL` can select an existing isolated test service;
there is no successful missing-service skip. The component fixture uses the
provider's explicitly named local-development plaintext profile; TLS acceptance
belongs to the B02 provider tests.

For an isolated offline SDK test feed, `--offline-config /path/NuGet.Config`
disables auditing only for those local locked restore commands. Repository audit
and release policy are unchanged.

Coverage includes empty, one and multi-batch rows; scalar/cardinality methods;
nullable versus missing/wrong-type fields; byte and row limits; oversized fields;
slow consumption; early break; throwing materializers; duplicate take/dispose;
method and enumerator cancellation; concurrent MoveNext/disposal; malformed wire
identity, unknown codec, NULL mismatch, UTF-8 and overlapping cells. The native
suite also holds a transferred batch across cursor retirement, abandons a close
wait, and proves its budget stays reserved until a controlled cleanup receipt.
An abandoned partial fetch cannot resume and still joins on close. An
indeterminate receipt retains isolation credit. A controlled cleanup failure
after a real provider join proves linked parent-token registrations are released
on the failed managed cleanup path as well.

Each successful provider scenario records actual native operation retirement,
session/lease closure, native batch/byte counters and managed call/copy retirement.
When reusable, the *same* session executes a second row query before it closes.
The JSON report separately records process RSS and CLR allocation observations.
The generated API checker rejects accidental exports and verifies the XML/reference
contribution. Source/lock/features/tool/native/executable hashes and raw command
logs are bound to each run. Component success does not establish assembled host,
pristine package, Windows, or sustained workload acceptance.
