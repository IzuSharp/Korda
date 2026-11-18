# External slow-client and shutdown cases

`transport_faults.py` supplements the B22 public API probe and external fault
matrix with actual B21 slow uploads, HTTP2 flow-controlled downloads, and SIGTERM
during a positively witnessed incomplete upload. It consumes an existing immutable
JIT or NativeAOT application and does not alter its business code or native assets.

```sh
python3 tests/Verification.EndToEnd/transport_faults.py \
  --artifact-directory /absolute/b21-artifact \
  --mode aot --provider sqlite \
  --certificate /absolute/test-cert.pem --private-key /absolute/test-key.pem \
  --output /absolute/new-transport-run
```

For PostgreSQL select `--provider postgresql` and supply `--postgres-bin` for the
accepted18.0 tools. The controller owns its PostgreSQL and application children
within one process invocation. Its short waits schedule specific witnessed
network conditions; they are not performance measurements or soak duration.

The upload remains incomplete while independent SQL sees no attachment reference.
After the rest arrives, the versioned response DTO, independently stored exact
replay bytes, and attachment hash/length must agree. The HTTP2 download advertises
a4096-byte stream receive window and deliberately withholds window updates after
actual positive DATA. During the hold it continues reading/parsing the socket,
rejecting excess DATA, reset or premature END_STREAM. After the hold,
the client resumes credit and requires the complete exact body and END_STREAM.
An arbitrary reset or timeout never passes as backpressure.

The shutdown case sends SIGTERM only after the server has written partial staging
bytes. The client remains connected and withholds the remaining body until the
application exits. The process must return successfully with its own CLOSED
resource-join receipt. Independent SQL must show no incomplete reference, and a
real restart must accept the protected same-key retry and return complete content.

Raw case assertions, timelines, child procfs snapshots, exact runtime witnesses
and unchanged artifact hashes are retained. Procfs RSS/threads/descriptors are
not aggregate managed-root or native-owner observations. The wider B22 observer
and long-duration profiles remain required separately.
The final receipt requires all three named cases and the completed unchanged
artifact check; an outer cleanup error or interruption cannot retain a passed
partial receipt.
