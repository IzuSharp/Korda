# A02 native negotiation fixtures

The production loader is in `src/Interop.Negotiation`. It consumes the canonical
A01 C ABI record and status definitions through `Interop.Abi`; no second ABI or
Core native runtime is defined here.

`fixtures/bootstrap.rs` is compiled into separate engine-prefixed Linux x64
libraries by `tests/Interop.Negotiation/verify.py`. It links the actual A01 Rust
writer and contains no domain instance, executor or worker. The positive alpha
and beta libraries have different bootstrap/probe exports and capability masks.
Deliberately malformed variants exercise minor/major/size/pointer/reserved/status
preflight failures in isolated JIT and AOT consumer processes. They are test
fixtures and are not included in any normal product release.
