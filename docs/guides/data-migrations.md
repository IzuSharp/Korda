# Explicit database migrations and catalog administration

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

`DataSource.Administration` is the application entry to the native SQLx migration engine. Install `Korda.Database.Sqlite` or `Korda.Database.PostgreSql`, import `Korda.Database` and the selected provider namespace, and build the source using its normal provider factory. The administration facade is part of `Korda.Database`; it introduces no separate runtime package or managed database driver.

Source construction, DI activation and reading the `Administration` property perform no database I/O and never run migrations. Deployment code must call `MigrateAsync` explicitly. Ordinary application startup is not a schema synchronization service.

## A minimal deployment

```csharp
await using var source = new DataSourceBuilder().UseSqlite("app.db").Build();
var bundle = new MigrationBundle(DatabaseProvider.Sqlite,
[
    new(1, "Create notes", "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT NOT NULL)"),
    new(2, "Seed note", "INSERT INTO notes VALUES(1,'first note')")
]);
var report = await source.Administration.MigrateAsync(bundle);
await source.Administration.ValidateMigrationsAsync(bundle);
var status = await source.Administration.GetMigrationStatusAsync(bundle);
var tables = await source.Administration.GetTablesAsync();
```

The executable sample under `samples/api/data-migrations` uses both actual providers and also supplies a bounded cancellation token and lock wait. Its PostgreSQL local-development TLS opt-out is explicit; production uses the provider's default verified TLS policy.

A `MigrationBundle` snapshots its input collection. Versions must be positive, unique and strictly increasing. Descriptions and SQL are immutable nonempty Unicode strings without NUL. These scripts are trusted deployment artifacts. No table name supplied by an untrusted caller is interpolated into a migration. Scripts cannot issue their own BEGIN, COMMIT, ROLLBACK, SAVEPOINT or other transaction control; the adapter owns those boundaries. Provider syntax and transactional-DDL restrictions are enforced by the actual SQLx provider.

`TargetVersion` applies only a forward prefix. It cannot downgrade an already applied version. Keep previously applied entries in every later bundle. Reapplying identical versions is idempotent; changing SQL, persisted mode or description is rejected. Missing applied versions and incompatible extra history also fail validation.

## Identities and ledger

`MigrationBundle.ContentHash` is lowercase SHA-256 over the canonical kind12 provider/entry control record before its hash field is added. Entries retain the exact UTF-8 SQL bytes, with no newline or whitespace normalization, and a separate SHA-256 of those bytes. The native boundary recomputes both identities before publishing an operation.

SQLx retains its own `_sqlx_migrations` table and actual SHA-384 checksum. `MigrationStatus.Checksum` is its 96-character lowercase hexadecimal representation, including for pending entries. The adapter never overwrites SQLx checksums with SHA-256.

`_nativeweave_migration_phases` records version, provider, SQL hash, originating bundle hash, description, transaction mode and execution phase. A transactional phase update commits with its DDL and ledger. A nontransactional PostgreSQL entry records `applying` durably before dispatch and `applied` after the successful SQLx ledger write. This metadata does not resolve an unconfirmed business commit by guessing from transport closure.

`ValidateMigrationsAsync` checks successful ledger entries, exact checksums and unfinished phases without creating or changing schema. `GetMigrationStatusAsync` returns immutable ordered comparisons; an unfinished PostgreSQL nontransactional phase appears pending when no successful ledger exists. A successful `MigrationReport.Completed` is true. Failed work throws its original classified error rather than returning a successful report with `Completed=false`.

## Provider transaction and lock boundaries

Every operation owns a dedicated administrative connection. It never borrows an application request session or enlists in a request transaction. One administrative connection is admitted per source, with `MaxWaiters` and `AdmissionTimeout` bounding queued callers. Independent sources/processes are coordinated by the real database lock.

PostgreSQL takes SQLx's session advisory migration lock before reading either ledger. Each transactional entry runs inside a tracked transaction and commits independently, so an error in a later entry does not roll back earlier confirmed entries. SQLx can return a checksum failure while its advisory lock remains held. The adapter closes its dedicated connection on every outcome; it never returns a possibly locked connection to the application pool.

SQLite rejects nontransactional entries before opening a connection. SQLx's SQLite migration lock is a no-op, so the adapter acquires a tracked `Connection::begin_with("BEGIN IMMEDIATE")` transaction before reading history. SQLx migrator work then uses nested tracked savepoints. Success explicitly commits the outer transaction; failure explicitly rolls back the entire selected batch, including its phase/SQLx ledgers. Raw SQL BEGIN is never used by the adapter. The pinned SQLx `Migrator::run_direct` entry is the same migration engine used by `run`, without its generic acquisition wrapper, which cannot satisfy the owning Host's erased Send future in this compiler composition.

WAL mode alone does not serialize migration history checks. Writer admission and busy/advisory-lock timeout are separately exercised. A new SQLite file may still produce a provider busy error during concurrent initial journal-mode setup; callers receive that failure and may explicitly retry after deployment coordination. No automatic write retry is hidden inside migration execution. Private in-memory SQLite is not available to a separate administrative connection and fails with a capability error; use a file-backed source for this profile.

## Nontransactional PostgreSQL recovery

An entry with `MigrationTransactionMode.NonTransactional` also requires `MigrationOptions.AllowNonTransactional=true`. This is for explicitly reviewed PostgreSQL operations such as a concurrent index build. The script and its effects are not claimed atomic.

A provider error or process termination can leave partial effects, an `applying` phase and no successful SQLx ledger entry. `ValidateMigrationsAsync` then fails. Before resuming, inspect the actual database state and repair or account for partial effects using an explicit administrative procedure. For example, a failed concurrent index build can leave an invalid index that must be inspected and removed before replay. Do not assume `IF NOT EXISTS` makes such an index usable.

A new, explicit `MigrateAsync` call with the identical bundle and opt-in can replay the unfinished script. No retry occurs automatically within the failed call. Different bytes cannot silently take over its persisted phase. This preserves a concrete recovery boundary without adding automatic schema repair or destructive downgrade behavior.

## Cancellation, cleanup and limits

Cancellation requests stop admission and future work while the admitted native operation remains owned. SQLite uses its worker progress handler to interrupt expensive SQL, then disables interruption for explicit rollback and connection close. PostgreSQL retains the currently dispatched SQLx command through its finite statement timeout, checks cancellation before the next phase/commit, and closes the administrative connection. A canceled task is not proof that all earlier entries were rolled back.

Source shutdown rejects new administration, wakes queued callers, requests active work to stop and retains the runtime until the administrative connection and operation actually retire. Managed callback roots, controls and input buffers retire separately from native connections/operations. The Linux tests observe both layers rather than infer one from the other.

Lock waits are positive and finite; `LockTimeout` defaults to thirty seconds. Command, input, field and buffered-result limits remain the source's immutable limits. Record counts include nested schema columns, and the encoded byte budget includes control-record overhead. There is no unbounded `fetch_all` catalog fallback. SQLx's own migration history read is preceded by a bounded check under the database lock. These are admission/encoded-allocation bounds, not a promise about the entire provider or process RSS.

## Catalog metadata

`GetTablesAsync` uses native parameterized provider catalog queries. PostgreSQL accepts bounded user-schema identifiers and excludes system schemas; null selects `current_schema()`. SQLite accepts `main` and does not expose attachments from another connection. Results retain exact table/column names, store types, ordinal order, primary-key membership and provider nullability. SQLite rowid aliases are distinguished from nullable composite or descending primary keys. PostgreSQL tables with no columns remain visible.

The returned lists are immutable snapshots. This API does not generate migrations, infer differences, discover CLR models, create provider rows or expose DataTable/reflection materialization.

## Linux validation scope

The owned native tests use actual SQLite and PostgreSQL18. They preserve independent raw-BEGIN failure, tracked outer rollback/commit and failed-second-migration whole-batch rollback oracles; PostgreSQL advisory-lock retention/close, permission failure, partial concurrent-index recovery, cancellation and checksum mismatch; and bounded nested catalog behavior.

The actual public C# wrapper runs in JIT and Linux x64 NativeAOT. Separate process tests contend on a held writer/advisory lock, forcibly terminate an admitted worker, observe database recovery and replay the identical script in new processes. SQLite termination is triggered only after uncommitted WAL spill is visible; PostgreSQL termination is triggered only after its transactional script is active on the backend. Subsequent replay is idempotent. Windows and other platform acceptance remain outside this Linux task run. The provider package/runtime/no-Rust composition follows B04; final pristine release consumption remains with A16/B24.
