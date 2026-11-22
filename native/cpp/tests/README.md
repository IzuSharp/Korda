# Native C++ contract verification

The C++ tests retain the database and shared Interop assertion families from product commit
`465cd0f90e6af05ec50eb581156240377cf0825b`. The corresponding C# consumers, API inventories,
wire schema, status values and fixture entry points remain unchanged. Test fixtures are built
only when `NW_BUILD_TESTS` is enabled and are not installed in the database product packages.

## Build and execute

Use the tool versions recorded in the root build documentation and the shared presets:

```sh
cmake --preset linux-x64-release
cmake --build --preset linux-x64-release --parallel 4
ctest --preset linux-x64-release

cmake --preset linux-x64-asan-ubsan
cmake --build --preset linux-x64-asan-ubsan --parallel 4
ctest --preset linux-x64-asan-ubsan
```

The sanitizer preset enables ASan, UBSan and leak detection. An environment that cannot run
LSan must report that limitation explicitly; a run with `detect_leaks=0` is not leak acceptance.
The shared libraries in the sanitizer build resolve sanitizer hooks from their test executables.
Release shared libraries still use the undefined-symbol link check.

Executables are in `artifacts/cmake/<preset>/tests`; maintainer libraries are in the sibling
`fixtures` directory. The `nw_interop_fixtures` target builds all preserved component adapters,
ABI probes and negotiation variants. The A01–A08 and A20 Python verifiers use
`tests/native_build.py` to configure, build, format-check, analyze and test these same targets.
They retain their managed JIT, Native AOT, API/XML and isolated negative cases.

## Assertion migration map

Paths in the first column refer to the frozen Rust commit, not current build inputs.

| Original assertion family | C++ test and unchanged consumer | Preserved invariant |
|---|---|---|
| `native/interop/abi/src/lib.rs`; A01 native layout/bootstrap probes | `native.interop`, `native.abi.bootstrap`, generated C and C++ layout probes; `tests/Interop.Abi` | Five complete record layouts, C calling conventions, status numbers, callback signatures, truthful capacity, untouched output tails, schema mutation rejection. |
| `native/interop/async/src/tests.rs` | `interop_tests.cpp`: `async_state`, `callback_and_owner_retirement`; `tests/Interop.Async` | Cancellation does not choose a terminal result; exactly one terminal and take; callback failure preserves the committed result; native sources and active callbacks independently block retirement. |
| `native/interop/callbacks/src/tests.rs` | `interop_tests.cpp`: `callback_state`; `tests/Interop.Callbacks` | Start rejection remains Created; the table is copied; reentrant stop does not join itself; source leases outlive callback quiet; stop/emit races never enter after retirement; bridge failure closes production. |
| `native/interop/errors/src/tests.rs` | `interop_tests.cpp`: `wire_and_errors`, `boundary_admission`, `owner_publication`; `tests/Interop.Errors` | Every error capacity preserves the original status; UTF-8 truncation is bounded; ordinary failures preserve admission; unknown exceptions are quarantined and poison ordinary admission; admitted calls finish; partial owner creation releases temporaries and publishes nothing. |
| `native/interop/memory` and `native/interop/lifetime` | Shared checked-extent and owner assertions; unchanged `tests/Interop.Memory`, `tests/Interop.Lifetime`, A20 property suites | Arithmetic overflow and alignment rejection; actual owner/backing accounting; failure-atomic publication; borrowed input copied before return; child backing retention; disposal and call leases. |
| `native/interop/execution/src/tests.rs`: zero/impossible limits, atomic budgets, independent cleanup, FIFO head cancellation, FIFO order, closing waiters, try-only profile, reentrant wake | `execution_tests.cpp`: `admission_and_waiters` | Operation, byte and cleanup reservations are acquired together before payloads; bounded metadata waiters cannot bypass their head; wakes occur outside the admission mutex; close retains issued credits. |
| Execution terminal retirement, real blocking close, cooperative close, wrong owner and duplicate start | `execution_tests.cpp`: `retirement_and_blocking`; unchanged `tests/Interop.Execution` | Task completion does not return payload credit; started blocking work must actually return; one permit cannot be used by another instance or twice; close results contain actual joined worker counts. |
| Execution future/body panic, blocking panic and completion panic | `execution_tests.cpp`: `actual_exceptions` | Real C++ body and completion exceptions are observed; unknown destructors are not invoked during quarantine; poison closes admission while the supervisor finishes retirement. |
| Execution 64 close/issued-permit races, independent workers and worker self-close | `execution_tests.cpp`: `close_races_and_ownership` | Every accepted or rejected start leaves an accounted obligation; engine instances own their workers; a worker may request close but cannot wait for its own join. |
| `src/Verification.Interop/native/src/lib.rs` | `fixtures/verification.cpp` plus the actual shared implementation; unchanged `tests/Verification.Interop` | C/C++/C# layout, numeric extent and UTF-8 differential properties; real callback/async schedules and ownership; stale-cookie and indeterminate-start child controls. |
| Rust query/binding tests and SeaQuery SQL/argument goldens | `native.query`; unchanged binding/native/Fluent consumers | Runtime model ownership, expression and alias scope, freeze/clone, ordered parameters, provider refusal and existing SQL output contracts. |
| Rust binding/control/row/scalar conversions | `native.contracts`, `native.scalar` | Canonical managed metadata and batch bytes, malformed-input rejection, codec configuration, exact provider conversion and refusal of lossy values. |
| Migration/catalog and provider fault assertions | `native.administration`, `native.provider` and their PostgreSQL runners | Migration checksum/history/idempotency, transactional rollback, SQLite lock modes and persistence, PostgreSQL TLS identity, cancellation/draining, broken connection replacement and bounded streaming. |

The optional execution conformance adapter uses actual bounded C++ worker threads, an owner
thread that joins them, separate normal/blocking work queues, admission reservations and a
close observer whose join is part of retirement. It does not invent successful retirement
from counters or a canceled wait. Database Host uses its own provider runtime; the optional
Core component creates no native executor merely by referencing the managed Core package.

## Isolated fatal cases and bounded fuzzing

`nw_error_probe` exercises recoverable C++ exception containment and four separate fatal child
cases: exception-object destruction, destruction while unwinding, failure in the termination
handler, and escape across a `noexcept` boundary. `nw_error_abort_probe` is compiled with
exceptions disabled and verifies explicit abort. These replace Rust-specific panic mechanism
tests while preserving the externally observable distinction between a translated native
error and a process that cannot recover. No fatal child is reported as successful retirement.

The A20 `nw_interop_fuzz`, `nw_interop_fuzz_negative` and `nw_interop_fatal` targets build the
original C oracle drivers with Clang sanitizers. The verifier retains the fixed seed,
20,000-run bound, deliberate numeric-oracle defect, minimized counterexample replay and
invalid-address/wrong-creator fatal controls. With the Release preset, its C drivers are
instrumented and its C++ libraries are not; the native sanitizer preset separately instruments
the shared C++ implementation and direct CTest suites. Neither bounded testing nor the fixed
schedule inventory claims exhaustive concurrency coverage.

The A02 runner checks every historical design input at its original pinned commit. It also
requires that commit to be an ancestor of the supplied current Design checkout and checks
unchanged current JSON, C# and C declarations. Guide changes introduced by DB-Scope-01 and
the C++ implementation decision are explicitly recorded in `pinned-design-inputs.json`;
they do not discard the historical hashes or alter the managed API checks.

## PostgreSQL retained provider scenarios

`fixtures/postgres.cpp` retains the two original B02 fixture exports. The private
`nativeweave_data_postgres` component has only its original bootstrap and capability bit zero;
it is distinct from the full `nw_database_postgresql` product asset. The original C# provider
consumer and sample continue to load these separate assets and execute all 11 scenarios in
both JIT and Native AOT. The standalone `nw_postgres_fixture_tests` runs the same entry point.

| Frozen `tests/Data.Postgres/native/src/oracles.rs` case | C++ case | Independent observation |
|---|---|---|
| `transaction_parent_retirement` | `transaction_parent_retirement` | A retired transaction cannot retain the pool lease; last parent closure is joined before retirement; later operations on a retired owner fail. |
| `tls` | `tls` | Real PostgreSQL 18 TLS, application name, hostname/root/expiry refusal, and explicit plaintext opt-in. |
| `timeout_rollback` | `timeout_rollback` | Server SQLSTATE `57014`, failed transaction `25P02`, explicit rollback and same backend reuse; an unreported failed statement never becomes a committed outcome. |
| `stream_and_admission` | `stream_and_admission` | Partial stream release, one active command, canceled bounded pool waiter, complete empty-result metadata, same backend reuse. |
| `codecs` | `codecs` | Actual canonical parameter bytes and provider conversions for UUID, decimal, timestamp, Unicode and NULL; oversized numeric refusal. |
| `savepoints_and_b05` | `savepoints` | Rollback-to retains depth, release removes it, committed row count is one, canceled commit is never dispatched, unsupported isolation starts no transaction. |
| `lost_commit` | `lost_commit` | A real framed TCP proxy sees and withholds server `CommandComplete(COMMIT)` and `ReadyForQuery(Idle)`; an independent connection sees the committed row while the client retains Unknown through cleanup. |
| `conflict_snapshot` | `conflict_snapshot` | Synchronization uses `pg_locks`, preserving the empty same-statement CTE result and the separately visible winner. |
| `quarantine` | `quarantine` | Stop after observed `PgSleep`, 20 ms protocol drain budget, physical closure, different replacement PID, backend termination and joined native owners. |
| `migration_lock` | `migration_lock` plus `native.administration` | A confirmed contaminated dedicated lease holds one advisory lock; the actual SessionMutating path physically closes it and an independent observer sees zero locks. Migration checksums/history/idempotency run against the C++ migration implementation. |
| `close_with_admitted_operation` | `close_with_admitted_operation` | An accepted operation keeps its pool permit while close waits; retirement follows completion and the same backend is reusable. |

The historical SQLx checksum-mismatch migration path retained its advisory lock. That
upstream defect is recorded in the B02 report, and the cleanup regression explicitly creates
the equivalent contaminated lease. It does not claim the C++ implementation uses SQLx or
recreates that defect. PostgreSQL stream stop follows the old bounded drain policy and never
sends a cancellation key merely to stop reading; a failed drain discards the connection.

```sh
python3 tests/Data.Postgres/verify.py \
  --postgres-bin /absolute/path/to/postgresql-18.0/bin \
  --native-build artifacts/cmake/linux-x64-release \
  --design-root ../NativeWeave-Design --output /absolute/fresh/evidence/b02
```

B01's shared schema generator now writes C# and `include/nw/generated/schema.hpp`. The C#
output is byte-identical to the frozen baseline. `native.contracts` checks the generated key
order and wire sizes against bytes emitted by the unchanged C# encoder. Its versioned golden
inputs permit native-only CTest; the B01 verifier also regenerates them in JIT and Native AOT
and feeds both versions to the same C++ executable.

## Compatibility provenance records

The compatibility maintainer fixture now builds five C++ profiles (`nw_compat_old`,
`new`, `major`, `layout`, `semantics`) against the real shared Interop implementation.
The old/new labels describe controlled ABI evolution profiles, not a released Rust binary.
The original two arithmetic assertions and deliberate semantics failure also execute directly
in CTest. The C# old/new JIT and Native AOT cross-pairs, metadata/hash/capability negatives,
compiled API evolution cases, and local package-closure controls remain required.

`Verification.Compatibility` emits component manifest schema **2** with `artifacts.native_lock`,
`native_lock_format: nativeweave-cpp-dependencies-v1`, and a structured C++ language standard
and Clang version under `upstream.toolchain`. The lock is the actual
`native/dependencies.lock.json`, bound by its own digest and source inventory. The private
reader still accepts archived schema **1** records with their original `cargo_lock` and
`msrv` fields; doing so does not run a Rust compiler or require Cargo files in this checkout.
Cross-schema reviews must explicitly enumerate the source-schema and toolchain changes;
all existing ABI, API, behavioral, artifact-hash, signature and immutable package checks
remain in force.

```sh
python3 tests/Verification.Compatibility/verify.py \
  --evidence artifacts/compatibility-acceptance \
  --native-build artifacts/cmake/linux-x64-release \
  --design-root ../NativeWeave-Design
```

The compatibility runner retains every historical Design digest. It separately records the
five server documentation files already removed by fixed DB-Scope-01 Design commit `8298dc4`;
this database migration neither restores those out-of-scope components nor silently ignores
changes to current public C, C# or JSON declarations. Development `--prepare` receipts are
marked as preparation and do not satisfy the formal clean-source gate.

The Core snapshot includes all seven retained Interop source components. Compiler-emitted
delegate methods are identified from the imported assembly, so SDK implicit global usings
cannot make their source-only identifiers diverge. Delegate declarations still require their
summary, parameters, return documentation and lifetime/cost policy. Five real XML mutation
controls reject each missing part and a missing ordinary public-member summary. The local
unsigned package-range control uses `zipfile` directly and preserves the `NWPKG012` rejection.

The PostgreSQL TLS scenario also sets test-scoped `PGSERVICE`, `PGCHANNELBINDING` and
`PGDATESTYLE` values and opens a fresh connection. It verifies that the explicit NativeWeave
policy and server DateStyle stay unchanged. This exercises the locked libpq configuration
patch; the product never rewrites process environment, and the old explicit rejection list
continues to reject the same policy override variables.

The B02 runner also launches the complete native suite in a separate process with an isolated
`HOME` containing deliberately invalid `.postgresql/postgresql.crt`, `postgresql.key` and
`root.crl` files. Its explicit trusted-root TLS connection must still succeed, and all original
TLS refusal cases remain active. Only that child receives the environment override; the
parent process and database servers keep their original environments. The receipt records
the test files and native binary hashes separately from the ordinary suite.
