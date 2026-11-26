# Public continuation boundary probe

This Linux x64 PostgreSQL Native AOT consumer observes exception routing when an
explicit public `MoveNextAsync().GetAwaiter().UnsafeOnCompleted(Action)` callback
throws after successfully reading a later batch. It uses an ordinary
`PackageReference`, a generated `DbTable` descriptor, a public prepared Fluent
query and the normal enumerator. It also observes a normal async-body exception
as a separate control.

This is a finite correctness experiment. It does not measure allocations, CPU,
throughput or latency and supplies no long-run credit. The report's historical
`private two-process correctness gate` scope and sentinel names are preserved
with the executed source; they do not indicate a private query transport.

The program installs an application-level exception handler for two exact
sentinel objects only. Other unhandled exceptions remain unhandled. Run each
package lane in its own process so this handler cannot affect another consumer.
The existing `nw_db_diagnostic_count_v1` export is used only to record ownership
and SQL/protocol counters. Queries and cleanup still use the public managed API;
the diagnostic does not submit SQL, inject callbacks or alter native state.

## Required inputs and boundaries

Use the repository's pinned .NET toolchain and the same compiler options for
both lanes. Copy this directory outside the Korda source tree before publishing
the application. The project intentionally pins `Korda.Database.PostgreSql`
`0.1.0-ci.1`, the local candidate identity used by this experiment. Each lane
requires its own package feed, restore cache, lock file and build directory;
never overwrite one same-version package in a shared cache.

Supply two independently prepared package inputs. This probe does not modify
production source or a package. The private flag experiment that motivated the
probe concerned `RetiredValueTask<TResult>` in `src/Interop.Async/AsyncOperation.cs`;
normal production must retain its reviewed continuation semantics. A change to
that private implementation requires its own source, package and build evidence.

Initialize a fresh task-owned PostgreSQL database with `fixture.sql`. It creates
`rcaa_items` with 4,096 ordered integer keys. The expected count, sum, minimum and
maximum are `4096`, `8390656`, `1`, and `4096`. The consumer configures two source
connections, 32-row batches and finite five-second admission/command limits.
Its PostgreSQL profile disables TLS for the local development connection used
by this fixture.

Initialize the database once before the two executions, using the same data for
both:

```bash
psql "$probe_connection" -v ON_ERROR_STOP=1 -f fixture.sql
```

For each lane, run an ordinary restore and Native AOT publish from the copied
directory. `probe_feed` is the absolute local feed containing that lane's package;
`probe_cache` must also be unique to the lane. Required transitive packages may
already be present in that cache or come from the declared public feed.

```bash
export NUGET_PACKAGES="$probe_cache"
dotnet restore RetiredInlineProbe.csproj -r linux-x64 -p:PublishAot=true \
  --source "$probe_feed" --source https://api.nuget.org/v3/index.json \
  --disable-build-servers
dotnet publish RetiredInlineProbe.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true \
  -p:UseSharedCompilation=false -p:IlcSingleThreaded=true \
  -m:1 -nr:false --no-restore -o publish

timeout --signal=TERM --kill-after=5s 30s \
  ./publish/RetiredInlineProbe "$probe_connection" "$probe_result" \
  "$probe_package" "$probe_lane"
```

The four consumer arguments are connection string, new result path, exact
restored `.nupkg` path, and a lane label. The program hashes the supplied package,
its executable and the actually loaded native provider library. The package
argument alone is not proof of what was compiled: retain each restore graph,
lock file, compiler response and publish log, and verify the referenced package
members. Also retain source hashes, exact commands, tool versions and exit codes.
Avoid reusing an existing result path; `File.Create` replaces its contents.

## Reading the observations

The runtime preflight and Fluent callback use distinct exception instances and
counters. The normal async-body control must reach its caller and allow another
session. The main query consumes and validates complete initial batches, then
selects the first initially pending later-batch Move. That selected ValueTask is
consumed exactly once by the explicit callback. The callback reads and validates
the returned row before throwing its sentinel.

Inspect the initially pending observation together with the actual callback and
exception stacks. Completion may race registration; an initially pending check
alone does not establish that a callback ran within an older executor frame.
`PublishRetired` may be inlined and absent as a separately printed AOT frame.
Retain what the stack actually shows. Compare global handler counts, the normal
async-body control, subsequent admission on the same source and all cleanup
results. The finite missing-handler observation is recorded as absence, not
manufactured into an execution failure or performance result.

Exit `0` means observations were captured without reported cleanup errors; it
does **not** mean that an experimental implementation preserved behavior.
Exit `3` means coverage was inconclusive, such as a missing runtime handler or
no pending later-batch Move. Exit `2` records another consumer failure. Preserve
every attempt instead of repeating until an expected result appears.
An outer process timeout or signal is a separate failed attempt, not an
inconclusive pending-refill observation.

The two-lane comparison covers this explicit throwing awaiter callback. It does
not establish behavior for every custom SynchronizationContext, cancellation
race, long synchronous consumer or concurrent workload. Native owner counters
cover the exported categories, not all managed/runtime objects or native heap
allocations. Results and the decision to retain or withdraw an experiment belong
in Plan's DB-Perf-01 evidence; this directory preserves the executable method.

The existing `db-perf-benchmarks` module owns this directory. Ordinary PR CI runs
its bounded harness gate; it does not mutate production flags, start this private
two-package experiment or run a long campaign.
