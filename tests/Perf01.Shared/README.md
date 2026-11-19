# Perf-01 shared-path profiling

`RetirementProbe` is an isolated package consumer using the existing test-friend
assembly identity. It does not alter product binaries or add public API. A serial
real SQLite/PostgreSQL scalar path records the existing start, terminal-copy,
take and retirement boundaries, CPU and managed allocation. It verifies each
scalar and observed native-quiescence/root-release invariant.

The observer has a measurable cost and cannot be included in the uninstrumented
before/after timing cohort. Callback-to-take and take-to-retirement intervals
localize waiting; they do not directly count native `Pending` responses, physical
database lock waits or connection-pool waits. Those values must stay unavailable
unless independently observed. Serial operations are a deliberate observer
constraint, not the application's high-concurrency acceptance workload.

Copy this project outside the checkout, give it an isolated NuGet configuration
with the exact rebuilt B24 feed, pin `NativeWeavePackageVersion`, and publish
Release Native AOT for `linux-x64` with the same CPU baseline as the main drivers.
Keep source, package, AOT and loaded-native identities with every result. Run it
only in a profiling window, separately from formal timing.
