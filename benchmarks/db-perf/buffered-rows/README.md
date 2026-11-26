# Local buffered public-API comparison

This builder makes an independent Native AOT consumer from a successful external
`query-phases` build. It reuses that exact PackageReference, project, SDK settings
and native assets. It copies source inputs only, rebuilds generated code in a
fresh directory, and never executes the consumer. Existing `bin` and `obj`
outputs are not copied. The original source, project, package, native library and
executable must retain their recorded hashes. The package's actual repository
commit must equal `--expect-source`.

```sh
python3 benchmarks/db-perf/buffered-rows/build.py \
  --consumer /absolute/path/to/ordinary-phase-consumer \
  --expect-source <full-package-repository-commit> \
  --output /absolute/path/to/fresh-buffered-consumer
```

The three routes call `QuerySelection.ToListAsync`,
`PreparedQuery.ToListAsync(session, parameters)`, and
`DataSession.QueryAsync(command, projection)`. Each verifies the actual returned
`IReadOnlyList<PhaseRow>` directly. There is no application-created replacement
list, streaming adapter, ABI wrapper, preload or changed native provider. The
fixture, dynamic/prepared SQL verification, typed Int64 values, generated DTO
projection and expected strings/nulls are inherited byte for byte from the
ordinary phase consumer and bound by separate hashes. SQLite's existing raw SQL
marker normalization remains explicit: Fluent uses `?`, raw SQL uses numbered
equivalent markers with the same ordered values.

Run only on the dedicated local Linux machine, using a fresh receipt path:

```sh
/absolute/path/to/fresh-buffered-consumer/out/DbPerf.QueryPhases \
  '<dedicated-connection>' /absolute/path/to/new-result.json 256 3 --rows 100
```

`--rows 1|100|1000` selects the same complete primary-key or bounded ordered
result as the source phase consumer. Use the existing independently initialized
fixture. The inherited optional positional `initialize` creates that fixture;
it also runs the requested probe and must stay outside any comparison window.

Success schema 4 has three sample durations: `ConstructTicks` covers query,
selection or typed parameter/command preparation; `SubmitToListTicks` covers the
public terminal from invocation through its complete owned list and required
cursor retirement; `ReleaseTicks` covers only explicit dynamic query disposal
after the terminal. **First-row latency is not observable through these buffered
APIs.** No field labels a total duration as first-row latency. `FirstExecutions`
means the first complete operation on each route, after shared setup, and does
not mean independent process-cold execution.

Full ordered result validation remains outside the three sample durations and
inside every whole-process window. Raw allocated bytes, all GC generations,
CPU, RSS, native owners, SQL executions and protocol submissions remain recorded
at the original boundaries. Business list/DTO/string allocations and harness
overhead remain in the totals. These counters alone do not prove framework-only
allocation or physical network round trips.

Failures retain the original propagation and exclusive-create `.failure.json`
evidence, including stage, route, exception identity fields and available owner
counters. No partial attempt receives performance credit. Every build preserves
its command, actual exit code, log, source diff, input/output hashes and exact
package commit; rebuilding a failed attempt requires another fresh directory.

Compare baseline and candidate with this same generated consumer source and
matched 1/100/1000-row arguments. Streaming phase measurements cannot establish
the benefit of a buffered-only change. This tool is local-only and adds no CI
job, performance gate or package production. It does not replace the ordinary
Dapper.AOT end-to-end reference or the required mixed-load runs.
