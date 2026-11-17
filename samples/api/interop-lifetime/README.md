# Internal lifetime consumers

This maintainer sample compiles against the actual internal Core implementation,
using IVT only for this conformance executable. Ordinary applications receive typed
domain owners and never adopt these raw fixture handles. `Program.cs` has a minimal
leased call and an advanced close-before-final-release example. The real Rust
fixture is `native/interop/lifetime`; no mock lifecycle algorithm is used.

`python3 tests/Interop.Lifetime/verify.py` builds and runs both examples in JIT and
Linux x64 Native AOT modes and records the source/executable hashes and logs.
The evidence state is product-executed for these component examples only after
that command succeeds; it is not assembled engine or cross-platform acceptance.
