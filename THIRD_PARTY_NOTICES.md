# Third-party notices

NativeWeave remains MIT licensed under LICENSE. The maintained native database product is C++20. Candidate native leaves contain selected source and patch hashes, exact linked input hashes, compiler provenance, ELF dependency records and original notices in `nativeweave/dependencies.json` and `nativeweave/licenses`.

| Selected component | Version | Use and original terms |
| --- | --- | --- |
| SQLite | 3.51.3 | SQLite provider, static amalgamation, public domain |
| PostgreSQL/libpq | 18.0 | PostgreSQL provider, static client library and frontend helpers, PostgreSQL license; explicit-configuration compatibility patch is identified in the lock |
| nlohmann/json | 3.12.0 | Shared bounded metadata parser, MIT |
| LLVM libc++/libc++abi | 23.1.5 | Linux static C++ runtime, Apache-2.0 WITH LLVM-exception; original combined license texts retained |
| SeaQuery query algorithms | 1.0.2 reference | Adapted query semantics/renderer algorithms, MIT; no Rust library is linked |
| OpenSSL | Platform 3.x | Dynamic cryptography/TLS, Apache-2.0; actual version/binary bound per producer |
| zlib | Platform 1.x ABI | PostgreSQL platform dependency, zlib license; actual binary bound per producer |

See `native/dependencies.lock.json` and `native/cpp/licenses`. SQLx and the evaluated KDb/sqlpp/Drogon/ADBC/WCDB projects are source-research references, not linked product dependencies. The DesktopPlatform configuration was an engineering reference; its AGPL implementation was not copied.

The .NET and Native AOT runtime/toolchain components keep their upstream licensing and SDK-produced notices. Platform libc, libm, libgcc_s and dynamic-loader dependencies are declared by the ELF profile. This note does not replace the generated artifact-specific inventory. A dependency or patch change requires a new reviewed lock and inventory before distribution.
