# Perf-01 completed long-load verification

`verify_long.py` reads one completed domain output directory. It creates only a
new verification receipt and never rewrites the driver, raw results, or previous
verdicts. Run it after load, recovery, idle observation, and process cleanup:

```sh
taskset -c 8 "$PERF01_PYTHON" tests/Perf01/verify_long.py \
  --group postgresql --output /absolute/completed-cell \
  --receipt /absolute/completed-cell/long-verification.json
```

The groups are `postgresql`, `sqlite`, `http`, `grpc`, and `codec`. Use the exact
frozen Python interpreter. Exit zero means that this additional long-load gate
passed; nonzero leaves a failed receipt with zero duration credit. An existing
receipt path is refused. Missing, interrupted, profiling-only, invalid, hash-
mismatched or internally inconsistent evidence cannot receive long-load credit. Raw record arrays and
JSONL are streamed, with a finite one-million-operation identity ledger.

## Conservative duration proof

Let **C** be the earliest controller-observed completion of a successful
ordinary measurement operation, and **D** the dispatch-start timestamp of the
last successful ordinary measurement operation. The first actual operation has
finished before C; the last actual operation cannot begin before its command's
dispatch-start D. Therefore **D − C** is a conservative bound on the span of real
operations. It must be at least **1800 seconds**. All timestamps in that
subtraction belong to the same controller monotonic clock.

The verifier also retains the original first-to-last dispatch-start span and the
separate IPC send lower bound, last dispatch-start minus the first dispatched
command's observed admission. Admission follows receiving that command over IPC
but precedes actual network/codec work. It cannot establish real-operation
duration by itself. The original dispatch span must also reach 1800 seconds;
the IPC lower bound is retained without an extra threshold because the first
dispatched command can be admitted after a later command has already completed.
These bounds do not measure uninterrupted CPU busy time or exact socket sends.
The fixed continuous schedule and full dispatch/lag/count gates establish the
active offered workload; D − C establishes the conservative real-operation span.

HTTP/gRPC endpoints are selected from the actual hashed frozen oracle tokens.
`http-cancel`, `grpc-cancel`, and `grpc-deadline` are excluded from both duration
endpoints. They remain included in the complete logical-success ledger and their
original exact wire checks. Ordinary protocol rows must also match the separate
client's scenario, protocol, successful logical status, and correctness result.

Schedule **1802 seconds** prospectively on both versions. This two-second guard
never assumes a pass: queueing, slow initial work, missing events, or exclusions
can still leave the observed D − C bound below 1800 seconds. Warmup, recovery,
idle time, and completion after the final dispatch receive no added duration
credit. Failed old cells retain their original verdict.

## Other required gates and limits

The original completed/valid flags, zero unexpected errors, full offered/sent/
admitted/completed success counts, correct native identities, recovery, and owner
drain remain required. The raw scheduled-arrival samples must satisfy the fixed
nearest-rank p99 limit of 500 ms, and actual measured application RSS samples must
stay at or below 256 MiB. This explicitly supplements the codec driver's narrower
`service_standard_met` flag; normal before/after RSS values cannot hide a measured
mid-run peak. Sampled RSS does not establish an unobserved instantaneous peak.

The receipt hashes every input it consumes and records the frozen driver/source/
artifact identity assertions. An input with a recorded prior hash is compared
against that hash. DB `observation.json` and application resource samples do not
have prior raw hashes in the existing summary: this verifier pins their current
bytes, checks their consistency and detects ordinary changes during verification,
but does not establish that arbitrary historical edits never occurred. It relies
on the original hashed domain driver's
payload, protobuf, persistence, TLS, cancellation and ownership oracles; it does
not execute the application again. It does not establish pairwise equivalence,
noise significance, absence of host interference, or completion of all ten cells.
Those remain separate requirements in the final frozen plan and report.

SQLite `business_retry_attempts=0` means zero explicit application business
retries. Internal SQLite busy-handler/sqlx retry counts are unavailable. This
clarifies any earlier prospective-policy wording saying “zero hidden retry”;
the running policy file is not rewritten, and the final frozen record must bind
this clarification.

`test_verify_long.py` contains finite synthetic evidence tests only. They cover
chunked JSON, completion versus admission bounds, probe exclusion, missing or
duplicate IDs, invalid outcomes, dispatch lag, mid-run RSS, codec tail/recovery
gates, changed hashes and profiling-only rejection. They do not run workloads:

```sh
taskset -c 8 "$PERF01_PYTHON" -m unittest discover -s tests/Perf01 -p 'test_*.py'
```
