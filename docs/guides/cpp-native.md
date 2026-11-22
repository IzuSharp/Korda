# C++ native database implementation

DB-CPP-01 replaces the fixed Rust product at `465cd0f90e6af05ec50eb581156240377cf0825b`. The C# API, package IDs, provider split, `db-wire/1` payloads, ABI signatures and database persistence formats remain the compatibility boundary. Actual completion is recorded in the [migration report](https://github.com/IzuSharp/Plan/blob/main/ledger/evidence/batches/DB-CPP-01/migration-report-linux.md); this guide alone is not a test receipt.

## Linux developer build

Install Python 3.11+, curl, tar/zstd, native build prerequisites and OpenSSL/zlib development headers. PostgreSQL also needs Make, pkg-config, Bison and Flex. Run `python3 eng/cpp_toolchain.py` once to install or verify the locked Clang, CMake and Ninja under the checkout; source `artifacts/toolchain/environment.sh` or select that CMake executable in the IDE. With sccache on PATH, the project enables its C/C++ launchers automatically.

Choose `linux-x64-release` for both databases and tests (the defaults), `linux-x64-sqlite` or `linux-x64-postgresql` for one, or `linux-x64-asan-ubsan` for instrumentation. Configure, build and test using the same preset:

```sh
cmake --preset linux-x64-sqlite
cmake --build --preset linux-x64-sqlite
ctest --preset linux-x64-sqlite
```

Linux PostgreSQL configuration prepares a project-private, patched client automatically. SQLite-only configuration skips this step. Full fault tests use `bash eng/postgres-tools.sh` to install the server and tools as well. Both paths validate compiler and archive hashes. Cache keys cover compiler, libraries, profile and build recipe. Increase the cache revision in `eng/postgres-cache-key.py` when forcing a refresh.

## Runtime ownership and source map

| Existing responsibility | Current maintained implementation |
| --- | --- |
| C ABI layouts, callbacks, errors, retirement, executor | `native/cpp/include/nw/interop.hpp`, `src/interop.cpp`; immutable `native/interop/abi/include/nativeweave_abi_v1.h` |
| Metadata, codecs, schemas, control records, row batches | `contracts.hpp/cpp`, `scalar.hpp/cpp`; generated C++ schema from the unchanged canonical specification |
| Fluent expressions, aliases, freeze/clone, parameter order, dialect SQL, prepared shape cache | `query.hpp/cpp`; runtime nodes accept business table descriptions without compiled C++ business types |
| Relations, CRUD, bulk progress, concurrency checks | `mutations.hpp/cpp` |
| SQLite and PostgreSQL physical execution | `provider.hpp/cpp` |
| Sources, finite pools/admission, sessions, operations, transactions/savepoints, bounded cursors/buffers | `host.hpp/cpp` |
| Existing C entry points and exceptions contained at the boundary | `host_abi.cpp` |
| Migration checksums/history/idempotence and catalog | `administration.hpp/cpp` |
| Original component test bridges and failure scenarios | `native/cpp/tests/fixtures`, `native/cpp/tests/ported-tests.md` |
| Native producer and twelve-package closure | `eng/task07_native.py`, `src/Packaging.Domain` |

All abbreviated source names above are under `native/cpp/include/nw` and `native/cpp/src`. Public C# business code still calls the same Fluent and async APIs. Synchronous binding copies borrowed parameters and metadata before returning. Frozen plans retain source identities, schema/codec descriptions and ordered placeholders. Capabilities reject unsupported operations before dispatch. Native C++ exceptions are translated to the existing status/error contract; they never cross the C ABI.

## Query semantics

The fixed NativeWeave validator and SeaQuery 1.0.2 are the semantic sources. C++ owns the mutable arena, generation-bearing expression/alias tokens, candidate-clause validation, immutable clones/plans and provider rendering. The original 52 SQL/ordered-parameter goldens remain exact. The C# driver also executes their fixed independently retained row oracles against both providers.

Values remain bound parameters. The source-bound prepared cache is bounded by both entries and bytes, excludes ordinary values from shape keys, and never owns a live connection or managed delegate. Every execution supplies its own copied parameter payload. Model/DTO mapping remains C# code generated or compiled against runtime metadata; neither an entity nor a DTO is part of the native build.

## PostgreSQL

The selected PostgreSQL/libpq source is 18.0. A connection uses nonblocking libpq I/O and bounded polling. Prepare/describe establishes metadata without waiting for a first row. The first fetch sends the prepared execution and immediately enables single-row mode; each PGresult is freed as it is consumed. User results are never accumulated into a provider-wide result vector.

Preserve the original provider's stop semantics: abandon the reader, drain through the final null result within the cleanup budget (five seconds in the product Host), and discard a connection whose protocol cannot be proven clean. Reader disposal does not send a cancel-key request that would newly abort an enclosing transaction. Caller cancellation and deadlines close business admission while the native owner remains responsible for drain/retirement. Transaction outcome and connection cleanup are separate; loss of COMMIT acknowledgment remains Unknown even if a later independent query observes durable data. A failed-transaction probe (`25P02`) followed by explicit rollback is a known rolled-back outcome.

TLS defaults verify identity and certificates. Explicit local-development plaintext remains opt-in. The libpq source has one small versioned configuration patch: it ignores ambient `PG*` defaults inside libpq, matching the SQLx reference. NativeWeave continues to reject the original thirteen forbidden overrides itself. The patch never changes the process environment and does not affect other libraries or concurrent applications. Its source/hash and original PostgreSQL notice are recorded in the dependency lock and producer evidence.

## SQLite

The 3.51.3 amalgamation is byte-identical to the Rust reference's bundled SQLite source. The original FTS, JSON, metadata, memory, URI, thread-safety, soundex and other selected compile options are retained. SQLite files, WAL/journal choice, synchronous mode, foreign keys and migration tables keep their existing formats and meanings.

Connections use prepare/bind/step/finalize on finite workers. The configured busy timeout remains observable by PRAGMA and preserves bounded BUSY/LOCKED waiting. Cancellation can interrupt SQLite execution without racing connection destruction. `sqlite3_get_autocommit` records actual transaction state after errors. Stream batches own copied cells; transferred batches keep their resource reservations until release, even after cursor closure.

## Dependencies and licenses

`native/dependencies.lock.json` locks upstream versions, download hashes, patches, consumed JSON/amalgamation sources, tool binaries and notices. Linux statically links libpq, SQLite and LLVM 23.1.5 libc++/libc++abi, uses nlohmann/json 3.12.0 headers, and dynamically uses the declared platform OpenSSL 3/zlib/glibc runtime closure. The two provider leaves include only their selected database implementation. The C++ runtime does not introduce a consumer dependency on libstdc++ or a C++ compiler.

Original MIT, PostgreSQL, public-domain, Apache-2.0/LLVM-exception and platform notices accompany candidate packages. SeaQuery's MIT notice accompanies the adapted query algorithms. SQLx, KDb, sqlpp11/23, Drogon, ADBC and WCDB were behavioral or architecture references, not linked dependencies. See the [design decision](https://github.com/IzuSharp/NativeWeave-Design/blob/main/docs/database/cpp-migration.md) for the finite comparison.

## Reproduce the Linux build

Install the platform development prerequisites (Python, curl, tar/zstd, bison/flex, OpenSSL and zlib headers), .NET SDK11.0.100 and the checksum-pinned native tools:

```sh
python3 eng/cpp_toolchain.py
. artifacts/toolchain/environment.sh
bash eng/postgres-tools.sh
export NW_POSTGRES_BIN="$PWD/artifacts/postgres-tools/install/bin"
export CMAKE_PREFIX_PATH="$PWD/artifacts/postgres-tools/install"
pre-commit install
pre-commit run clang-format --all-files
cmake --preset linux-x64-release
cmake --build --preset linux-x64-release
ctest --preset linux-x64-release
cmake --install artifacts/cmake/linux-x64-release
python3 eng/check_native.py tidy
bash eng/verify-cpp-sanitizers.sh
```

The presets are `linux-x64-release` and `linux-x64-asan-ubsan`; both inherit `native-base` (Ninja, C++20/C17, compile-command database). Their build trees are separate. Release installation is `artifacts/native-install/linux-x64-release`. Sanitizer execution enables ASan, UBSan and leak detection. A host where LeakSanitizer cannot initialize is incomplete for that gate; the Linux CI run must supply the result.

`windows-x64-release` selects LLVM clang-cl, MSVC ABI/dynamic runtime and Windows SDK; `macos-release` selects upstream clang/clang++ with Apple SDK. These are toolchain conventions for subsequent platform tasks, not tested product RIDs in this Linux batch. Local Apple Clang requires recording its actual version and Xcode build number independently.

For source-bound candidate production, commit a clean tree, select an isolated PostgreSQL18 test endpoint and the historical declaration inputs described in README, then run `python3 eng/build.py verify`. This invokes the same presets and packages their installed Release assets. `eng/verify-cpp-clean.sh` separately builds, packages and consumes in the pinned fresh Linux image with no Rust installation. No public release is performed.

For this migration's additional old/new acceptance, use `python3 eng/build.py verify --compare-rust-reference`; the DB-CPP-01 pull request enables the same option in CI. It retrieves and verifies the fixed old candidate archive and runs the versioned `tests/Migration.Cpp` consumer in separate processes. An offline archive can be supplied through `NW_RUST_REFERENCE_ARCHIVE`. This finite migration comparison is separate from ordinary future C++ product builds and does not introduce a permanent dependency on an expiring legacy workflow artifact.
