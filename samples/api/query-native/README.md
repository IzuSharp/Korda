# Native query maintainer example

B11 is internal. Application examples use B13's strongly typed Fluent surface in
`Korda.Database`. [Examples.cs](Examples.cs) demonstrates the actual narrow
integration seam: static table/DTO mapping, metadata registration, native filter and
projection calls, clone/seal into an immutable reusable shape, execution through the
real B04 Host, and deterministic reverse-order disposal. It is compiled into
`tests/Query.Native/Korda.Query.Native.Tests.csproj` and called by both the JIT
and Native AOT runs; it introduces no separately distributed package or public FFI.

The native consumer in `tests/Query.Native/native/src/tests.rs` supplies the direct
SeaQuery SQL/ordered-argument comparison, provider result oracles and advanced
JOIN/CTE/set/window/NULL cases. `tests/Query.Native/Program.cs` replays them through
C# and the canonical ABI against both real providers. Exact source and artifact
identities are recorded by `tests/Query.Native/verify.py`.

Use the command in [the guide](../../../docs/guides/query-native.md). It requires
PostgreSQL 18 tooling and a committed, built B04 native Host; ordinary application
users do not install Rust or run this maintainer verifier. No absent-provider path
is recorded as an acceptance pass.
