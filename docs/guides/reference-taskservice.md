# B21: idempotent work-item reference service

The application in `src/Reference.TaskService/` composes the accepted Database,
ORM and Server modules through **PackageReference**. C# owns the business
handlers, static entities/DTOs, validation, hashes and filesystem protocol.
SeaQuery/SQLx execute the database work, and the Rust Server Host owns HTTP,
HTTP/2, TLS and stream backpressure. The executable is internal application
code; it adds no public framework type, package or native ABI.

This implementation follows `C-REFERENCE-001` 0.4.0 at Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`. It does not introduce a durable
job/attempt/claim/complete business state machine. The outbox has delivery
leases; the operations table contains only replayable results committed in the
same transaction as their business effects.

## Installation and explicit configuration

Use .NET SDK 11.0.100 / C# 15 and a maintainer-packed feed containing
`NativeWeave.Database.Sqlite`, `NativeWeave.Database.PostgreSql` and
`NativeWeave.Server` plus their transitive dependencies. The application project
defaults `NativeWeavePackageVersion` to `0.1.0-preview.1`; an explicit compatible
candidate can be selected through that MSBuild property. Normal consumer
build/run has no explicit RID and no Rust toolchain invocation. Native AOT
publication additionally needs the Linux linker prerequisites.

The B21 package probe generates an isolated consumer and a NuGet source-mapping
configuration. For an existing candidate feed, map `NativeWeave.*` to that feed
and other packages to nuget.org, then run:

```sh
dotnet restore src/Reference.TaskService/Reference.TaskService.csproj --configfile /absolute/path/to/consumer/NuGet.Config
dotnet build src/Reference.TaskService/Reference.TaskService.csproj -c Release --no-restore
dotnet run --project src/Reference.TaskService/Reference.TaskService.csproj -c Release --no-build -- --config /absolute/path/to/reference.json
```

The same source is compiled by the minimum/advanced consumer at
`samples/api/reference-taskservice/`. Its default minimum profile installs only
SQLite and Server; `ReferenceIncludePostgreSql=true` selects the advanced
provider composition with a separate lock file. The source is also copied
outside the source tree by the package verifier, which checks ordinary
model/DTO/handler edits against
unchanged native bytes. Public publication of domain packages is a separate B24
gate; the HelloWorld preview alone does not establish that those packages are
published.

Create a protected configuration file outside the repository. This structural
example uses placeholders that must be replaced by the operator:

```json
{
  "provider": "sqlite",
  "database": "/absolute/path/service.db",
  "consumerDatabase": "/absolute/path/consumer.db",
  "blobDirectory": "/absolute/path/blobs",
  "cursorKey": "<base64 of 32 to 64 random bytes>",
  "principals": [
    { "token": "<opaque bearer token, 16 to 256 printable ASCII characters>",
      "tenant": "tenant-a", "principal": "alice", "canWrite": true }
  ],
  "listenAddress": "127.0.0.1",
  "port": 5000,
  "runWorkers": true
}
```

The PostgreSQL profile selects `"provider":"postgresql"` and an explicit
connection URI. Its normal TLS policy remains the provider default. Only an
explicit local-development endpoint may select
`"allowLocalDatabaseTlsDisable":true`. For the HTTP/1+HTTP/2 TLS profile, add
`tlsCertificatePem` and `tlsPrivateKeyPem` paths. Without those files the
listener explicitly selects HTTP/1; no plaintext HTTP/2 capability is inferred.

Authentication compares a hash of the configured opaque bearer credential with
fixed-time comparisons. The authenticated tenant/principal and named read/write
policy determine scope. Client tenant headers and full keys do not enter default
telemetry. The example is an explicit authentication SPI consumer; it does not
claim to supply an identity provider or token-rotation service.

| Command | Effect |
| --- | --- |
| `--config FILE` | Explicitly apply the checksum-bound provider/consumer migrations, then start the service |
| `--config FILE --migrate-only` | Apply those same forward migrations and exit |
| `--config FILE --outbox-once` | Execute at most one 16-row claim/publication batch and emit its delivered count |
| `--config FILE --maintenance-once` | Reconcile confirmed recovery, run one bounded retention batch and collect eligible old orphan files |
| `--golden-vectors` | Emit v1 hashing vectors without configuration, native loading or database I/O |

The blob directory has one process owner. Stop its HTTP service before running
`--maintenance-once` against that directory. This makes local filesystem
ownership explicit; cross-process/distributed blob-store ownership is not a
supported profile. `--outbox-once` uses its independent DB leases and does not
open the blob directory lock. `runWorkers:false` selects operator-driven outbox
execution. Automatic mode claims once per second; maintenance is an explicit
operator command.

## REST contract and stable results

| Operation | Successful behavior and bounds |
| --- | --- |
| `POST /v1/items` | Static JSON `{ "title": "..." }`; one printable ASCII `Idempotency-Key` of 1–128 characters; returns 201 with a durable stable result |
| `GET /v1/items?limit=100&after=CURSOR` | Tenant-filtered `(created_at,item_id)` keyset page; 1–100 rows, authenticated versioned HMAC cursor |
| `GET /v1/operations/{id}` | Tenant/principal-authorized durable result; 200 contains its original response bytes |
| `PUT /v1/items/{id}/attachment` | Write-authorized item; required `Content-Length` up to 64 MiB, `Idempotency-Key`, and `If-Match` expected integer version; returns stable attachment metadata |
| `GET /v1/items/{id}/attachment` | Authorized DB reference before a bounded file stream; emits length, version ETag and content SHA-256 |

Titles retain their original Unicode code points, must contain nonwhitespace
text, cannot contain NUL, and are limited to 1,024 UTF-8 bytes. JSON wire input is
limited to 1 MiB, rejects unknown members and uses explicit source-generated
metadata. Keys use characters `0x21..0x7e`; whitespace and repeated header
values reject. UUIDs use lowercase dashed `D` format. Times are Unix
milliseconds stored as signed 64-bit integers.

A created response has the following fixed v1 fields and order:

```json
{"responseVersion":1,"operationId":"...","itemId":"...","title":"...","createdAt":0,"version":1}
```

The originally serialized UTF-8 bytes and status are persisted. Same-scope,
same-key, same-hash requests return those bytes and the original status.
Different hashes return 409 `idempotency_conflict` without changing the winner.
Attachment binding returns `responseVersion`, `operationId`, `itemId`,
`contentHash`, `contentLength` and the incremented item `version`. Its key/hash
check precedes the version CAS, so a replay remains successful after that first
request advanced the version. A new key with a stale version returns 412
`version_conflict`.

`scope` hashes authenticated tenant, principal and endpoint semantic identity.
Operation recovery checks both authenticated identities and the stored semantic
scope. Lists are tenant-filtered; their cursor also binds the principal. A
cursor from another identity, an invalid MAC or malformed framing returns 400.
The keyset includes the unique item ID as the tie-breaker and does not claim a
cross-page snapshot.

### Exact v1 request-hash bytes

The byte stream begins with uint32 little-endian `1`. Each string consists of a
uint32 little-endian UTF-8 byte count followed by exactly those bytes. Strings
use strict UTF-8, no BOM and no Unicode normalization. Integer suffixes use
signed int64 little-endian. SHA-256 is stored as lowercase hexadecimal.

- Create strings: `POST /v1/items`, tenant, principal, title.
- Attachment strings: `PUT /v1/items/{id}/attachment`, tenant, principal, actual
  item ID, lowercase content SHA-256. Integer suffixes: content length, expected
  version.
- Scope strings: `scope.v1`, tenant, principal, endpoint semantic literal.

`--golden-vectors` executes the product framing functions. The independent
Python vectors in `tests/Reference.TaskService/golden-v1.json` include ASCII,
Unicode, composed/decomposed code points, field-boundary separation and integer
attachment fields. This is a fixed application protocol, not a general JSON
canonicalizer. `responseVersion=1` separately identifies persisted response
serialization.

## Native transactions, idempotency and Unknown

The four application tables are `operations`, `items`, `outbox` and
`attachments`; the explicit PostgreSQL/SQLite scripts adapt binary columns to
BYTEA/BLOB. The migrations preserve `(scope,request_key)` uniqueness, stable
operation IDs, tenant/item primary keys and explicit concurrency tokens. The
native migration subsystem supplies its separate checksum ledger. The outbox
consumer has its own SQLite database and `receipts` table.

PostgreSQL uses the existing ReadCommitted transaction policy. One trusted,
typed `SqlCommand` performs `INSERT ... ON CONFLICT(scope,request_key) DO
NOTHING RETURNING operation_id`. When no row is returned, the next SQL statement
is the typed native-backed `FindAsync` lookup of the winner, giving PostgreSQL a
fresh statement snapshot. This is intentionally not a single CTE snapshot and
not an upsert that modifies the winner. The existing `UpsertAsync` API requires
nonempty conflict-update assignments, so it cannot express this claim. The
narrow expert SQL recipe is fixed application text with typed parameters; it
adds no C# SQL builder, dialect translator or provider driver.

SQLite sets the existing `SqliteOptions.BeginMode=Immediate` and a five-second
busy timeout. Writes occur in short Rust-owned transactions; the operation
result, item or attachment mutation and outbox row commit together. A task row
is never separately committed as `InProgress`.

COMMIT has a 15-second independent deadline. Before dispatch, a durable recovery
identity protects the operation. The token controls native admission; after
dispatch an HTTP cancellation cannot reinterpret effects. A
`CommitOutcomeUnknownException` becomes 503 `outcome_unknown` with operation ID,
authorized recovery URI, correlation ID and bounded retry guidance if the
connection remains usable. It is not a successful create or a reliable queued
task. Recovery returning no row means `outcome_unconfirmed`; it never proves a
rollback. A retry uses the same unique-key-protected protocol.

Each HTTP operation closes its session with a finite 100 ms wait. A Pending
snapshot keeps engine-owned parents, callback roots and budgets. No request
handler follows Pending with an implicit `await using` join. A bounded
transaction-retirement continuation may remove a recovery marker only after
the actual retained transaction reports `RolledBack`; it captures no request
context or DI scope. Unknown and rollback-unconfirmed records remain protected.
There are 32 application protection-retirement slots, reserved before mutation
DB admission and held until the native and filesystem obligations end. Slot
exhaustion returns 503 `recovery_cleanup_capacity`. This bound also applies
after the native pool releases its connection. The process joins these owned
tasks after handler/source retirement on both normal and exceptional host exit,
before releasing the blob-root lock. The HTTP error or business result is not
replaced by a cleanup error.

The seven-day replay window is explicit in `Idempotency-Retention-Seconds`.
Maintenance first reconciles a durable matching result, then removes delivered
outbox audit rows older than seven days, and finally eligible old operations.
Any remaining outbox or unresolved recovery reference prevents deletion.
Absence alone cannot remove recovery protection. Replay after expiration is
not guaranteed; permanent duplicate prevention would require another explicit
storage policy.

The operator runs `--maintenance-once` while the serving process is stopped.
Each recovery, upload, outbox and operation phase processes at most 100 entries.
The small atomic `maintenance.json` file records each phase's scan position;
outbox/operation positions advance only after confirmed commit. A short or
empty page wraps that phase to the start. Thus protected oldest rows remain
protected without starving later eligible records, including across process
restarts. These cursors are housekeeping progress, never proof of a business
outcome.

## Outbox delivery and the independent effect oracle

One claim transaction selects at most 16 eligible events, uses PostgreSQL
`FOR UPDATE SKIP LOCKED` or SQLite Immediate admission, and records a 30-second
lease with an incremented attempt count. Its transaction ends before external
publication. The independent SQLite consumer inserts one stable event ID and
effect in its own transaction. Repeated publication verifies the same payload
hash and increments `delivery_attempts` while `effects` stays one.

Only after consumer confirmation does the source record delivery. Lost
acknowledgement can cause a later duplicate attempt. Retry backoff doubles from
the attempt count, capped at five minutes; after 16 attempts the row enters an
observable failed state instead of silently disappearing. Shutdown stops new
claims; an admitted publication uses its own finite deadline. This is
at-least-once delivery with consumer deduplication, not an external exactly-once
claim.

Publication has a 15-second admission token; the independent consumer uses the
existing 30-second native COMMIT deadline. Shutdown joins actual admitted
publication and native cleanup rather than promising completion within the
admission token's duration.

The external tests independently reset a delivered event into eligibility and
read both durable stores. Their attempt count and effect count are separate
oracles; an application log line alone cannot prove one business effect.

## Attachments, durability and finite resource ownership

Authorization and a short item lookup finish before upload starts. The client
upload holds no DB session. The Rust-backed request stream copies at most 32 KiB
at a time into a hash/file sink, reserving managed staging while native bytes
remain charged. The body length is checked against both declared length and
the 64 MiB route cap; compressed uploads reject. Downloads authorize the DB
reference before opening a file, release the DB session, then use the existing
charged `Results.Stream` path.

The blob-store default is four simultaneous uploads and a 256 MiB disk budget,
including temporary files and recovery metadata. Whole-upload reservations are
conservative and include metadata capacity. There are at most 8,192 retained
upload or commit-recovery records, with another 8 KiB reserved for atomic
maintenance progress. Exhaustion returns an explicit capacity failure before
new reference work. A configurable grace period defaults to one
hour and cannot be below one minute.

An upload file is hashed and flushed to stable storage. Its recovery manifest
records the content hash before the same-filesystem immutable rename, and the
blob and staging directory changes are flushed before the short DB binding
transaction begins. A successful reference cannot point to an incomplete
upload. A failed reference transaction may leave an orphan, because DB and
filesystem are not one distributed atomic transaction.

Staging manifests record uploading, renaming, prepared and recovery states.
Active and hash-bearing unresolved manifests protect their blobs. Positively
known pre-DB failures release protection; confirmed rollback does so only after
actual transaction retirement. Confirmed matching durable results may be
reconciled. Old inactive stages without a possible DB reference and old
unreferenced/unprotected immutable blobs are eligible for collection after the
grace period. Equal hashes never grant cross-tenant access: every download
requires an authorized attachment row.

Commit recovery records are written and flushed through `.json.new`, atomically
renamed and directory-flushed before COMMIT dispatch. A crash during their
initial write therefore cannot publish a malformed final recovery record.
Abandoned temporary manifest files cannot authorize DB binding and become
eligible after the same grace period; complete uncertain records remain
protected. Upload reservations include all not-yet-written bytes. Metadata
publication/removal is serialized with quota observation, and a file already
removed by cleanup contributes no retained bytes.

Linux directory fsync and same-filesystem rename are actual application calls.
Other platform persistence implementations and acceptance are explicitly
deferred; the platform boundary remains cross-platform compilable. Process-crash
tests cannot prove power-loss durability, disk-loss survival or a distributed
object-store consistency model.

The Server keeps ReferenceSmall's 512 connections, 32 HTTP/2 streams per
connection, 256 active/retiring requests, 128 metadata-only waiters, separate
64 MiB managed/native payload budgets, 4 MiB per request per side, 32 KiB chunks
and 32 KiB headers. The database pool is limited to 16 connections and 64
waiters. Default request duration is 120 seconds. Application payload caps do
not claim to bound TLS, driver, allocator, CLR or whole-process RSS.

## Verification and documentation applicability

`api-applicability.json` records the consumed package/native paths and the lack
of new exported framework APIs. XML documentation is built with the executable;
the static application metadata and JSON context are compiled in both the
canonical app and sample. The normal consumer has no ProjectReference to
framework source. The copied-consumer checks modify a managed model, DTO and
handler and compare all native-module hashes across JIT and actual Linux x64
Native AOT execution.

The independent B21 controller exercises both real databases and the Rust
HTTP/1 and TLS HTTP/2 paths, durable idempotency/scope/CAS effects, exact request
and response bytes, keyset boundaries, length limits, 64 MiB streaming,
outbox duplicate attempts, orphan/retention protection and restart. A protocol
oracle specifically holds the PostgreSQL winner's COMMIT while a conflicting
request waits, and separately drops an actual COMMIT acknowledgement to
validate Unknown recovery.

Actual rows, raw observations, source/native/package/executable identities and
independent review are recorded in `Plan/ledger/evidence/B21/`. B21 does not
claim B22's fault matrix or 30-minute/24-hour/72-hour soaks, B23's workload
comparison, B24's final release composition, pristine no-Rust environments or
Windows acceptance from its local component evidence. A missing row remains
unaccepted until its own required execution completes.
