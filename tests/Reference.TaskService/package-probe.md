# B21 independent package matrix

`package_probe.py` copies the reference application into a directory outside the
source worktrees and consumes an existing exact NuGet feed. It builds the copied
application with the ordinary .NET SDK, then passes its actual JIT or Native AOT
executable to the independent HTTP/SQL controller in `run.py`.

The feed is a maintainer-produced input. This runner never invokes Cargo, creates
new framework behavior, rebuilds the native modules, or repeats earlier component
acceptance. B21 acceptance remains separate from B22 fault/soak qualification and
B24 pristine-machine/release acceptance.

## Prerequisites

- Linux x64 and the repository-pinned .NET SDK from `global.json`.
- Native AOT linker prerequisites and `openssl` for a local test certificate.
- PostgreSQL **18.0** binaries, including `initdb`, `postgres`, `pg_ctl`, and `psql`.
- Python dependencies from `requirements.txt`, including HTTP/2 support.
- An existing local feed containing `NativeWeave.Database.Sqlite`,
  `NativeWeave.Database.PostgreSql`, `NativeWeave.Server`, and their complete
  NativeWeave package closure at one exact version.

Both the application and PostgreSQL must be children of the same invocation on
executors that isolate loopback between commands. The runner creates and stops
its own loopback-only PostgreSQL cluster. It does not require a shared server or
reuse the earlier component runner's 180-second timeout. Each HTTP/SQL matrix
cell has a 1,200-second outer limit; this is a deadline, not a claimed soak time.

## Full invocation

Use an evidence directory outside every Git worktree. Replace the example paths
with the actual already-built feed and PostgreSQL installation:

```sh
python tests/Reference.TaskService/package_probe.py \
  --feed /absolute/path/to/existing-feed \
  --postgres-bin /absolute/path/to/postgres-18/bin \
  --output /absolute/path/outside-worktrees/b21-package-matrix
```

`--source` optionally selects another checkout root, or its
`src/Reference.TaskService` directory. `--runner` optionally selects a different
current B21 `run.py` during integration. The controller and its sibling Python
modules/golden fixture are frozen into the attempt before execution. The default
package version is `0.1.0-preview.1`; use `--version` only for an existing exact
feed at another agreed candidate version.

No existing output directory is overwritten. Use a new directory for changed
source, package bytes, runner code, SDK pin, or package-probe code. Original
failed logs and partially completed evidence remain intact.

## Matrix and ordinary C# edits

| Application | Providers | Runtimes | Transport | Rows |
| --- | --- | --- | --- | ---: |
| Original copied source | SQLite, PostgreSQL | JIT, Native AOT | HTTP/1 plaintext, HTTP/1 TLS, HTTP/2 TLS | 12 |
| Ordinary managed edits | SQLite, PostgreSQL | JIT, Native AOT | HTTP/2 TLS | 4 |

Every row uses a fresh SQLite file or a newly created PostgreSQL database. The
baseline PostgreSQL HTTP/1 rows additionally execute `--postgres-oracles` in both
runtimes. Other transport rows retain the complete ordinary HTTP/SQL oracle
without repeating those specialized PostgreSQL cases.

The edited copy adds `Item.DisplayTitle`, uses it in the existing typed
`ItemView` projection, adds an observable `ItemView.ManagedEdition` JSON property,
and changes the ordinary `GET /v1/items` C# handler to emit a response header.
These are edits to application files in the copied consumer only. After the
normal build/publish and complete selected HTTP/SQL row, an independent HTTP
request must observe both the new DTO property and handler header. All three
native module hashes must still match the original feed.

A small internal module initializer reports
`RuntimeFeature.IsDynamicCodeSupported` to stderr. Actual service logs must show
`True` for JIT and `False` for Native AOT. This witness adds no framework API and
does not change the golden-vector JSON written to stdout.

TLS uses a newly generated private test CA/certificate with `localhost` and
`127.0.0.1` subject alternative names. The HTTP client explicitly trusts that
certificate and validates the server name/IP. TLS verification is not disabled;
the controller also checks actual HTTP/1 versus HTTP/2 negotiation.

## Incremental execution and recovery

Run only the first useful cell while integrating:

```sh
python tests/Reference.TaskService/package_probe.py \
  --feed /absolute/path/to/existing-feed \
  --postgres-bin /absolute/path/to/postgres-18/bin \
  --output /absolute/path/outside-worktrees/b21-package-matrix \
  --variants baseline --providers sqlite --modes jit --protocols http1
```

Once the exact same inputs are ready for the remaining cells:

```sh
python tests/Reference.TaskService/package_probe.py \
  --feed /absolute/path/to/existing-feed \
  --postgres-bin /absolute/path/to/postgres-18/bin \
  --output /absolute/path/outside-worktrees/b21-package-matrix --resume
```

`--modes`, `--providers`, `--protocols`, and `--variants` accept one or more values.
`--build-only` restores and builds/publishes the selected variants/runtimes without
claiming any execution row. The edited profile requires `http2-tls`; selecting
only another transport does not silently substitute an edited transport row.

Resume verifies input identities, copied consumer sources, retained build output,
resolved package/lock snapshots, and successful row evidence before reusing them.
Completed rows are skipped. A failed row is retried in a new numbered attempt
directory. A failed build similarly receives new artifact and metadata paths.
No old log or failed artifact is overwritten. A corrected input requires a new
output directory, because a previous successful result belongs to its actual
earlier bytes.

The summary is `partial` until all **16** required rows have actually passed.
Selected successful work and build-only runs can exit successfully with that
explicit partial status. A failed invocation records
`failed_with_retained_progress`; the successful cells remain separately listed.

## Evidence and boundaries

`progress.json` contains the combined immutable-input manifest, original and
per-attempt Git provenance, exact commands/log hashes, generated consumer source
hashes, resolved package/lock graphs, package and native hashes, JIT/AOT artifact
inventories, runtime witnesses, per-cell oracle summaries, and failed attempts.
`summary.json` lists completed and remaining rows without implying another
platform, a soak duration, or a public release.

Each copied project has explicit .NET 11/C# 15, nullability, implicit usings,
warnings-as-errors, exact direct PackageReferences, and its own NuGet source
mapping. Empty local `Directory.Build.props` and `Directory.Build.targets` stop
parent repository imports. The independent restore rejects ProjectReferences or
unexpected global package folders. A separate consumer cache is used throughout.
`cargo`, `rustc`, and `rustup` are trapped; a recorded invocation fails the run.
This demonstrates no consumer Rust invocation, not an OS image without Rust.

Private generated test keys, bearer/cursor configuration files, mutable database
storage, and attachment blobs remain local. The public evidence index selects
logs and receipts while excluding private configuration and blob content.
Do not copy the entire output directory into Plan: retain the selected receipts,
logs, source/package/native identities, and any required immutable artifacts
using the existing evidence policy.
