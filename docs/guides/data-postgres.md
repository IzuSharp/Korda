# PostgreSQL provider component

B02 implements the PostgreSQL provider over SQLx 0.9.0 and the canonical B01/B07
contracts. The native component accepts an existing SQLx pool lease and engine
runtime, executes and streams on that fixed physical session, and supplies the
same-session B05 transaction driver. It contains no managed PostgreSQL driver,
second physical pool, global Tokio runtime, or application-specific native model.

The exact design input is NativeWeave-Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`. Hashes and the exact current public
symbol inventory are in `src/Data.Postgres/design-inputs.json` and
`public-api.expected.json`. The compiled XML contribution is
[the PostgreSQL component API reference](../../src/Data.Postgres/api-reference.md).

## Current component and downstream composition

The component exports `PostgreSqlTlsMode`, `PostgreSqlOptions`, and
`PostgreSqlDataSource.Engine`. Constructing options and reading Engine perform no
native load or database I/O. Engine preflight loads the exact maintainer-built
component asset and advertises only `postgres.component.v1`. That capability
identifies the bootstrap/profile component; it is not a usable datasource host
and advertises no B04 datasource, session, cursor or transaction ABI entrypoints.
The Rust adapter remains available to the B04 native engine by ordinary static
crate composition.

B02's packet explicitly permits actual backend/component ABI verification without
adding a reverse dependency on B04. Consequently these **exact pinned declarations**
are recorded in `src/Data.Postgres/downstream-composition.json` for B04 to compose
with its actual `DataSource` and `DataSourceBuilder` owners:

```csharp
public static NativeWeave.Database.DataSource Create(
    string connectionString, Action<PostgreSqlOptions>? configure = null);

public static NativeWeave.Database.DataSourceBuilder UsePostgreSql(
    this NativeWeave.Database.DataSourceBuilder builder,
    string connectionString, Action<PostgreSqlOptions>? configure = null);
```

The first belongs to `PostgreSqlDataSource`; the second belongs to
`PostgreSqlDataSourceBuilderExtensions`. The component supplies the configuration
snapshot, immutable kind8 record and Rust connect/session hooks for that
composition. No placeholder datasource type, throwing factory, alternate public
factory or second host is introduced. Complete host AOT/NuGet consumption remains
B04/B21/B24 work using these exact component commits. This assembly is not added to
the automatic preview publication scope by B02.

## Configuration and preflight

```csharp
using NativeWeave.Database.PostgreSql;
using NativeWeave.Interop;

var options = new PostgreSqlOptions
{
    RootCertificatePath = "/etc/nativeweave/postgres-root.pem",
    ApplicationName = "Orders",
    ConnectTimeout = TimeSpan.FromSeconds(15)
};
// VerifyFull is already the default. Configuration alone opens no connection.
EngineDescriptor descriptor = PostgreSqlDataSource.Engine;
```

The minimum and advanced executable examples live in
`samples/api/data-postgres`. The advanced component example supplies an explicit
maintainer asset path to `Inspect`; its hash, architecture, ABI and advertised
component capability are still verified. The facade's compiled manifest records
the real release asset SHA-256. A maintainer refreshes that digest during the
verification build, commits it, and verifies the final bytes. An ordinary
application consuming a future assembled package does not run Cargo.

`PostgreSqlOptions` is mutable configuration; the owning factory snapshots it
before admission. The snapshot validates defined enum values, strict UTF-8 text,
absolute root paths, and a positive connect timeout of at most one hour. The
application name has a 256-byte input cap; PostgreSQL can truncate its display.
Snapshot creation performs bounded copies; it does not open files or connections.

The native connect profile takes a PostgreSQL TCP URI with explicit username and
database. It rejects all query-string options, fragments, Unix-socket host escapes,
and ambient `PG*` connection-policy variables. SQLx's public connect options inherit
those variables, so the component rejects their presence instead of mutating the
process environment or accessing private fields. An absent URI password is made
explicitly empty to disable implicit `.pgpass` fallback. Supply credentials through
the transient outer source configuration; neither config nor provider errors
implement a secret-bearing debug display. Statement logging is disabled.

Production defaults require **VerifyFull**: an authenticated certificate chain and
matching hostname. A supplied absolute PEM root path selects the trusted root;
otherwise native system roots apply. There is no Prefer, Require, hostname bypass
or automatic downgrade mode. `DisableForLocalDevelopment` must be explicitly
selected for plaintext development. TLS failure does not switch modes.

The serialized provider options use the canonical B01 control envelope, kind8:

| Field | Exact type | Meaning |
| --- | --- | --- |
| 1 | u32/type1 | TLS: 0 VerifyFull, 1 explicit development plaintext |
| 2 | strict UTF-8/type4, optional | Absolute root-certificate path |
| 3 | strict UTF-8/type4 | Application name |
| 4 | timeout nanoseconds/type3 | Exact positive connect deadline |

The native adapter consumes the shared checked envelope parser, validates exact
field order/types/widths and allowed values, and copies the admitted fields. Unknown
kinds, duplicate/missing fields, overlaps, malformed UTF-8 and incompatible types
reject before opening. The JIT and AOT tests send the actual managed-produced record
across a narrow test-only ABI, then connect using that snapshot and verify the
actual server `application_name`.

## Session and command ownership

B04 owns pool size, waiter admission, parent/child ownership and runtime shutdown.
B02 accepts one `PoolConnection<Postgres>` and records its real backend PID. A
single atomic operation gate rejects concurrent use instead of serializing user
work in an unbounded queue. An admitted worker owns its connection and parameter
snapshot independently of the business waiter. Closing waits for admitted workers,
including workers that have not acquired the connection mutex yet. It cannot
return a permit ahead of their native accessors.

The fixed session profile sets a finite server `statement_timeout` (30 seconds by
default); cleanup has an independent five-second deadline. SQLx's statement cache
is explicitly bounded (64 entries by default; zero disables it). Configured SQL
input and B07 parameter limits are checked before submission. The B07 `Parameters`
owner is the only scalar binder; every statement builds fresh fallible SQLx
arguments. There is no alternate unchecked binder or managed query implementation.

`Command::generated` is a Rust-local trusted boundary for already capability/type-
checked native SQL. SQLx `AssertSqlSafe` is used only at that boundary; it is not SQL
sanitization. Audited expert SQL carries an explicit `SessionMutating` category.
That category rejects inside a framework-owned transaction and requires physical
closure when its session ends. Ordinary generated operations must not smuggle
transaction control or session mutation into the ordinary category. B04/B11 own
public command admission and this classification; no raw SQLx connection escapes
to managed callers.

Native errors retain a bounded SQLSTATE, category and correlation ID. Raw SQLx
error text, SQL, parameter values and connection strings are not retained. Ordinary
SQLSTATE errors are separate from transport/protocol damage. A failed PostgreSQL
transaction produces `25P02` until rollback; a completed business Task is not
permission to reuse it.

## Streams, stop and retirement

`Session::stream` returns a Rust-local `Stream<Item=Result<PgRow,ProviderError>>`
with an independent cloneable stop/join control. The producer reserves its one-row
channel slot **before** requesting the next SQLx row. The observed provider-owned
retained-row peak is therefore at most one; B06 separately owns decoded rows,
wire batches, byte budgets and result-shape validation. `Session::describe` uses
SQLx's public prepare/statement metadata API, so B06 can validate empty results.

Dropping the business receiver or calling `request_stop` performs an atomic store
and notification. It does not block, send on a bounded I/O channel, acquire a
connection mutex or manufacture a PostgreSQL cancel request from private keys.
The native worker remains responsible for dropping the cursor, sending SQLx Sync
and draining through ReadyForQuery. The retirement receipt follows that cleanup.
A drain deadline closes the session to new work, quarantines it, and awaits physical
closure while `PoolConnection::close` retains its pool permit. Unknown protocol
state never returns to the pool. `detach` and `close_on_drop` are not used as proof
of retirement. Closing a socket is local retirement; it does not prove a remote
transaction's effect or immediate server cancellation.

For clean sessions, the provider explicitly awaits SQLx return-to-pool maintenance;
B04 retains the pool/runtime until all session and operation receipts are joined.
A returned connection is tested by its backend PID, not merely by a successful
query on a possible replacement. A session-mutating command instead requires
physical closure and a replacement lease.

SQLx Execute limit0 requests the full result from the server. The one-row channel
is application backpressure, not server fetch batching or an RSS cap. SQLx protocol
buffers, the caller's current row, B06 wire buffers and CLR DTO copies may coexist.
This component makes no throughput, memory superiority or zero-allocation claim.

## Transactions and Unknown

The B05 `TransactionDriver` implementation uses the same retained session and
SQLx's public tracked transaction manager. PostgreSQL supports ReadCommitted,
RepeatableRead, Serializable and read-only transactions; unsupported isolation
rejects before BEGIN. Savepoint begin/release update tracked depth. Rollback-to uses
SQLx's public `rollback_ansi_transaction_sql(current_depth)` on the same connection
**without decrementing depth**, so the later tracked release addresses the correct
savepoint.

SQLx does not expose the COMMIT-versus-ROLLBACK command tag through its public
transaction-manager result. Before COMMIT, a harmless same-session query verifies
that the transaction is usable. A `25P02` result invokes tracked rollback and
returns explicit `Confirmation::RolledBack`; it cannot be reported as committed.
The ordinary operation gate prevents another application command between the probe
and COMMIT. Missing trustworthy COMMIT confirmation is passed to B05 as an error;
B05 preserves Unknown independently of subsequent cleanup. Successful local
rollback/closure never overwrites that original Unknown business outcome.

The response-loss regression forwards COMMIT through a controlled **test-only**
proxy, observes server `CommandComplete(COMMIT)` and `ReadyForQuery(Idle)`, discards
them, and checks durable state over another connection. The real B05 coordinator
reports Unknown while the observer sees one committed row. This is exercised by
both the JIT and actual Native AOT component consumer.

Migration execution belongs to B08. The B02 regression runs SQLx's real migrator on
a dedicated lease, observes the advisory lock retained after checksum mismatch,
then transfers that lease to the provider's session-mutating retirement path and
joins physical closure. The observer verifies zero advisory locks afterwards. A
failed close does not prove the remote lock disappeared.

## Verification and scope

Run from a Linux x64 maintainer checkout with the pinned Rust/.NET toolchains and
PostgreSQL18 binaries available:

```sh
python3 tests/Data.Postgres/verify.py \
  --output artifacts/B02/attempt-001 \
  --postgres-bin /path/to/postgres18/bin
```

Offline SDK/AOT packages can be selected with `--offline-config /path/to/NuGet.Config`.
After an intentional native asset change, use `--refresh-profile` during preparation,
commit the refreshed manifest, then run final verification without that switch.
`--write-reference` similarly regenerates the compiled reference before the final
unchanged-source run. No test silently succeeds when PostgreSQL or TLS inputs are
missing. The verifier owns three disposable TCP server instances (verified TLS,
expired certificate and plaintext) and shuts them down in its own process lifetime.

The real regression groups cover all six Design PostgreSQL oracles: timeout and
rollback; early stream drop; codecs; durable COMMIT response loss; conflict CTE old
snapshot requiring the next SELECT; and migration advisory-lock cleanup. Additional
rows cover TLS wrong hostname/untrusted root/expired certificate/plaintext opt-in,
B05 savepoint rollback-to/release, cancellation before commit dispatch, unsupported
isolation, bounded admission, empty-result metadata, drain-timeout retirement,
server-terminated sessions and restored pool permits. Managed tests add exact option
snapshots, malformed native records and component preflight. The same real provider
suite executes through a separate test-only ABI in JIT and Linux Native AOT.

Evidence binds exact source, locks, features, toolchains, native and managed artifact
hashes, server identity, certificate identities, commands, outcomes and cleanup.
The test ABI is not shipped as an application-facing provider surface. Linux x64
component completion does not claim Windows acceptance, pristine full-package
consumption, an assembled B04 host, long-running load results or performance gains.
Independent review and final batch acceptance remain separate from these tests.
