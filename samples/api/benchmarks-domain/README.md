# Minimal and advanced B23 consumers

These runnable external consumers use actual independently built Native AOT
applications. They do not create a product package or public framework API.
The [guide](../../../docs/guides/benchmarks-domain.md) gives the complete inputs,
configuration, immutable evidence, timing and interpretation rules.

`minimal.py` selects real create/replay/recovery, keyset and streamed-attachment
functional cells. Supply `--package-probe`, `--comparators`, and a new `--output`.
It makes no performance claim. `advanced.py` selects the full primary,
PostgreSQL/TLS/HTTP2, diagnostic and minimal-versus-request-DI matrix. Supply the
instrumented application manifest and PostgreSQL 18.0 binaries as documented.

The advanced command also defaults to functional validation. Timed work requires
an explicit `--phase calibrate` or `--phase measure`, an actual isolated-window
note, all three immutable route manifests, and a matching prior calibration for
confirmation. Runtime measurements never run a compiler or rebuild Rust.

The C# consumer path is the compiled B21 application. Its benchmark-only advanced
counter/DI extension is `src/Benchmarks.Domain/instrumentation/DomainInstrumentation.cs`,
compiled by `build_instrumented.py` against the exact packed APIs in both Release
and Debug Native AOT. It remains separate from the primary product executable.
