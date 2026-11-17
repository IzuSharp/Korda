# Packaged Linux Core consumers

Owner: A14. The sample source is the exact task-owned `tests/Interop.Abi`,
`Interop.Lifetime`, `Interop.Memory`, `Interop.Errors`, `Interop.Callbacks` and
`Interop.Async` consumer source copied by `tests/Packaging.Linux/verify.py`.
It compiles against real packed Core/ABI bytes; no source reference or Design stub
is used. The copied project, source hashes and locks are in each receipt.

Minimal development: run `python3 tests/Packaging.Linux/verify.py`. It uses normal
RID-free restore/build/run for each independently created consumer.

Advanced deployment: run with `--require-baseline` on Ubuntu 24.04, then transfer
the generated AOT directories and execute `runtime.py --require-tool-absence` in
the matching runtime-only environment. Native linker prerequisites are required
for publication, and no Rust compiler is required for application builds.

Evidence state is recorded per actual invocation. Script presence does not mean
the minimum distro, complete A07 closure or runtime-only transfer passed.
