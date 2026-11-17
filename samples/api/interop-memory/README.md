# Immutable buffer consumers

`Program.cs` demonstrates the exact public NativeBuffer API: a minimal synchronous
read/copy and an advanced bounded asynchronous stream export with deterministic
disposal. Shared `Fixture.cs` is maintainer setup using an actual Rust-owned buffer;
ordinary applications receive a NativeBuffer from their typed engine instead.
No design throwing stub or duplicate ownership algorithm is used.

Run `python3 tests/Interop.Memory/verify.py` for JIT and Linux x64 Native AOT
execution, source/binary hashes and compiled API/XML/reference checks. A successful
run establishes product-executed component examples, not final engine release,
loader re-acceptance or Windows acceptance.
