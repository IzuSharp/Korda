# Diagnosing the interrupted SQLite calibration

`diagnose_sqlite.py` is a B23 diagnostic driver. It reuses a frozen calibration's
dataset, title bytes, HTTP/TLS mode, arrival rate, warmup and bounded client
concurrency in a fresh process and disposable database. It does not produce an
A21 calibration or confirmation cohort. In particular, observation overhead and
the diagnostic driver's scheduling differ from A21, and reaching the original
in-flight cap is retained explicitly.

The default application remains the immutable accepted B21 package consumer.
The optional observer is a separate Native AOT build from the same accepted
source, with one middleware catch that records bounded exception metadata and
rethrows the same exception. The observer verifies byte-identical native assets.
No retry, response mapping, database configuration or business logic is changed.

For the preserved `calibration-001`, execute the driver from a separate worktree
at remote diagnostic checkpoint
`b8ca9a6a8a27dc04a7902597d747ea79889d5ae3`
(`task/Task-07-B23-diagnosis-linux`). Its tree
`36ab7e7535d67b83c5a56ff59551023acc7d94df` exactly matches the original local
execution commit `0eaf14cc2977820bfee5c485aafb3576325abbb3`. That checkpoint keeps
the imported `http_target.py`, `common.py`, A21 and golden oracle at the original
calibration's exact bytes. The driver relocates file paths, never expected hashes.
A newer checkout containing the repaired controller will intentionally fail those
old input hash checks. Do not rewrite the manifest to make it pass; use the frozen
worktree or its byte-identical exported source bundle.

## Captured evidence

Each request records its logical operation, actual protocol, status, monotonic
request duration and synthetic idempotency-key hash. Failed responses retain the
full body length and SHA-256, at most 16 KiB of body bytes, and bounded response
headers. Authorization and cookie headers are excluded. The complete expected
business outcome is checked, including replay and recovery status/body equality.
A failed outcome stops new work; already admitted operations drain. Independent
SQLite queries reconcile all confirmed mutations and retain the failed keys'
durable rows, including original response bytes when present.

Application shutdown is bounded separately from operation completion. A process
watchdog stops a stalled diagnostic after 150 seconds and records that forced
termination does not establish native retirement. Successful runs retain the
application's actual `CLOSED` receipt and recheck artifact hashes. Interrupted
builds and diagnostic runs are distinct records, never retroactive successes.

Example, using an existing exact calibration and isolated output directory:

```sh
python src/Benchmarks.Domain/diagnose_sqlite.py \
  --calibration /path/to/calibration-001 \
  --output /path/to/adverse-baseline-new \
  --attempts 2 \
  --cases warm-cycle-high-sqlite-http1 warm-cycle-high-sqlite-http1-tls warm-create-large-sqlite-http1
```

`--build-observer /path/to/accepted-package-probe --output /path/to/new-build`
creates the optional observation copy. Run the same command above with
`--observer /path/to/new-build` to select it explicitly. The original source,
dataset and native identities must remain available and match their manifests.
Run diagnosis separately from performance and resource-calibration windows.

## Linux diagnostic findings, 2026-11-18

Six unchanged-binary process runs reproduced two empty HTTP 500 responses after
approximately five seconds. Six separately identified observation-copy runs
reproduced both create and replay failures. The caught exception was
`DatabaseException`, category `Provider`, provider code `5` (`SQLITE_BUSY`),
matched to the failed response by the idempotency-key hash. This classifies the
observed reproductions as SQLite lock contention at the finite busy budget; it
does not identify the exact native statement or lock holder.

The replay failure sequence was create 201, replay 500, then recovery 200 with
the original durable response bytes. Every diagnostic's independent durable
oracle passed and every application reported successful database/consumer
cleanup and disposed server state. A create failure had no durable record for
its failed key; an unsuccessful replay preserved its original record and one
item/outbox effect. These are finite observed results, not a general proof of
every execution schedule.

The original interrupted calibration did not capture the exception or failed
response bytes. Its failures remain adverse historical evidence; their
individual causes cannot be retroactively proven from these new runs. The
observation copy and direct diagnostic scheduling are not the original benchmark
executable or a bitwise replay.

## Interpretation and next measurement

The original success-only benchmark cells remain invalid. Neither SQLite busy
responses nor a saturated generator can be accepted as successful throughput.
Keep them in the workload report, with their original source and artifact hashes.
No data-loss or duplicate-effect defect was observed in this diagnosis, and it
does not by itself justify a product change or automatic write retries.

A new operating-point calibration may use a lower offered rate when identified
as a new profile before execution, applied equally to all comparable routes and
followed by independently reviewed measured noise bands and fresh confirmation.
Preserve the high-rate adverse results beside it. The new results cannot establish
that the old rate passed. B22's required correctness and complete long-duration
profiles remain independent final acceptance dependencies for B23 and B24.

This is private benchmark/diagnostic tooling. It adds no public product API,
NuGet dependency, generated API baseline or native contract.
