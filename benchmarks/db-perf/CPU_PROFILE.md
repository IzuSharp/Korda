# DB-Perf-01: separate CPU and native-plan diagnostics

This workflow diagnoses remaining costs using the original Linux x64 executables
from runtime artifact `14953201071`, run `45461269267`. It runs on a separate VM;
it cannot qualify a formal throughput/latency observation or any of the eight
long segments. It does not modify the running campaign request, production code,
packages, sysctls or access permissions. There is no JIT consumer.

## Inputs and symbol identity

`cpu-profile-request.json` freezes the actual artifact ZIP, tar, complete bundle
manifest, original request, and all eight route ELF identities. Extraction is
bounded and uses the tar data filter; the original frozen harness checks every
file hash, size, mode and symlink. The source checkout for symbol reconstruction
is fixed to `da153c22ef1093fccc3233ec76bfda96e6cc59df`.

The original bundle intentionally omitted `*.dbg`. The existing external builders
are therefore invoked against the original provider feeds, SDK and source. They
use the original fresh application paths from the retained build receipts, which
avoids changing paths embedded in deterministic compilation inputs. A recovered
symbol file is used only if the reconstructed ELF is byte-identical to the
original, all three GNU build IDs agree, and the original GNU debuglink filename
and CRC match the symbol file. Reconstruction is never permission to execute the
new ELF. All application runs use the original bundle paths.

The two Korda AOT consumers require matched symbols. Dapper is sampled when its
symbols can be recovered under the same checks; otherwise its explicitly optional
cell remains unexecuted with the failure retained. No mismatched symbols are
silently accepted. Original bytes and recovered symbols are copied to a separate
`symfs`; adding debug information never changes the immutable bundle.
The observer also retains the ELF files it actually sees in the owned client's
executable mappings, including system libraries. Their device/inode identity and
SHA bind the copied images. Existing symbol tables support function attribution;
a file without debug information is not claimed to provide full source lines.

## Five fixed CPU cells

The sequence is PostgreSQL after, before, direct C++ with per-operation physical
prepare plus product confirmation, direct C++ with preparation reuse plus ordinary
drain, then Dapper when its exact symbols are available. Each is a single 30-second
C1, prepared, closed-loop execution of all 35 cases on a fresh copy of the same
seed. The two native CPU contracts change two factors and must be reported as
complete contract comparisons, not as a single-factor preparation experiment.

The observer first owns a child waiting on a private pipe. `exec` preserves that
PID and start identity. `perf` attaches only to that held PID with inheritance for
its threads, acknowledges enable through a private control FIFO, and only then
does the parent release the original executable. The original executable path,
SHA, PID/start ticks and mappings are observed after exec. The collector process
is never mistaken for the application by the resource observer.

Sampling uses the actual installed perf binary, recorded SHA/version, the
`cpu-clock:u` event at 99 Hz, DWARF stacks of 16 KiB, and `CLOCK_MONOTONIC`.
Unprivileged sampling is tried first. Only a recorded permission denial may use
the existing noninteractive sudo permission, and only for the same owned PID and
its inherited threads. That path requires a root-owned perf executable that is
not group/world writable. An outer timeout bounds the privileged collector.
Before either collector starts, the ordinary observer creates an empty private
output file with exclusive, no-symlink creation. The existing perf output path
is truncated in place; after collection, its device, inode, owner and mode must
still match, and an ordinary observer read must succeed. This preserves readable
raw data even when collection needs sudo, without changing ownership or modes.
Neither path uses system-wide, CPU-wide, other-user-PID, raw tracepoint, BPF or
kernel sampling. No capabilities or sysctls are changed.

Raw perf data includes setup so it is retained in full. Reports and sample ledgers
use the original application's exact steady start/end monotonic timestamps. Every
parsed sample must belong to the held application PID and that interval. Lost
records, unknown symbols, limited sample counts and unobserved wall-time waiting
must remain visible. CPU samples are not operation counts or proof of a physical
network round trip. User-space stacks cannot explain kernel/system CPU by
themselves. Profiles are not subtracted from formal CPU measurements.

The original consumer's correctness, full-case coverage, operation/row ledger,
native/package identity and drained owners are still checked. Raw process
samples and PostgreSQL owner/accounting receipts are retained. There is one
workload attempt per fixed cell; a failed or partial result is not replaced with
a preferred sample. A permission retry happens while the child is still gated,
before its workload starts. The original error and cleanup failures are retained.

## Independent native query-plan pair

After CPU sampling has stopped, two separate native executions enable the original
auto_explain library. Both keep `protocolConfirmation=drain-only`; only physical
preparation changes between `per-operation` and `reuse`. Each uses the original
350-operation warmup plus one complete 35-operation cycle. This captures each of
`range`, `offset` and `early-stop` eleven times, together with the complete
ordered workload, real PostgreSQL logs, parameters, plans and business results.
The original plan collector validates the full workload. No perf collector runs
in these plan cells. The records do not label a plan generic or custom without
direct evidence from that execution connection.

## Commands and retention

```bash
python3 benchmarks/db-perf/cpu_profile.py validate
python3 benchmarks/db-perf/cpu_profile.py self-test
python3 benchmarks/db-perf/cpu_profile.py run \
  --source /absolute/frozen-da153-checkout \
  --output /absolute/fresh-diagnostic-output
```

The runner command is intended for its separate GitHub workflow VM. The short
controls validate ELF/symbol corruption and PID/window rejection without a
database, compilation or perf execution. Workflow push activation is limited to
the new profile request path, with an independent concurrency group.

The artifact preserves bootstrap/image failure, original request/runtime identity,
symbol commands/receipts, matching symbols, raw perf files, filtered reports,
application identity, operation results, process samples, native plans and all
available failure records. Database data directories and a duplicate copy of the
input runtime archive are excluded. An artifact alone does not imply that its
cells completed. Read `diagnostic.json` and the individual error/coverage records.

## Tool documentation

- [Native AOT diagnostics](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics)
  describes native CPU sampling and the need for matching native symbol files.
- [Linux perf security](https://docs.kernel.org/admin-guide/perf-security.html)
  distinguishes unprivileged per-process user-space monitoring from broader
  scopes. A paranoid value of 2 alone does not prove a particular host permits
  collection; the actual tool invocation is retained.
- [perf-record](https://man7.org/linux/man-pages/man1/perf-record.1.html)
  documents PID inheritance, clock selection, DWARF collection and acknowledged
  control FIFOs. These documents establish available mechanisms, not observed
  success on the current diagnostic VM.
- [perf output implementation](https://kernel.googlesource.com/pub/scm/linux/kernel/git/stable/linux/+/47f05f71ad988c3180bdba807151e6e86ed75aa3/tools/perf/util/data.c)
  opens an existing output with truncation and only backs up a nonempty prior
  file. The diagnostic also verifies that behavior on the actual installed tool
  by requiring the precreated inode and ownership to remain unchanged.
