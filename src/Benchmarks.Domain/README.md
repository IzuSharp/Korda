# B23 assembled domain benchmark tooling

This directory adapts actual accepted B21 HTTP applications to the accepted A21
external controller. `http_target.py` is an independent client/observer, never a
managed product server or database fallback. The production route still loads
the unchanged Rust Database and Server modules.

`profile-v1.json` declares workload inputs and the calibration/noise policy.
`run.py` supports separate `functional`, `calibrate`, and `measure` phases. A
confirmation cohort requires matching immutable calibration inputs; interrupted
or failed cells retain their observations and cannot become benchmark passes.
The default functional phase makes no performance claim.

The three route identities are inherited from A21:
`direct-rust`, `nativeweave-aot-rust`, and `managed-aot`. Comparator applications
and their independent build manifests live in `comparators/`. Unsupported
provider/protocol/resource profiles remain explicit in the matrix.

Latency, histogram, coordinated-arrival accounting and bootstrap analysis remain
in `src/Benchmarks.Harness/nativeweave_bench.py`. HTTP-client admission is not an
internal server/database admission timestamp. The adapter records the actual
application process separately from itself, including procfs CPU/RSS/thread/fd
observations and independent SQL/blob reconciliation.

The task owns no new public application API or NuGet package. Benchmark-only
instrumentation and comparator plumbing stay internal. B22 retains fault/soak
correctness acceptance; B24 retains final clean-machine release acceptance.
