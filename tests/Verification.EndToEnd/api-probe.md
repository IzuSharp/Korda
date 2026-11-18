# B22 packed public API composition

`src/Verification.EndToEnd/ApiProbe` is an internal executable. It consumes the
unchanged SQLite, PostgreSQL, Server, gRPC and MCP packages. It adds no framework
API, ABI, managed SQL driver or alternative application host. Ordinary application
code and source-generated routes/JSON are compiled for JIT and Linux NativeAOT.

The probe starts the real Rust TLS host and uses an ordinary `HttpClient` to send
actual HTTP1.1/HTTP2 traffic. This client is a conformance driver, not the product
network runtime or a performance baseline. A private test certificate is trusted
by exact certificate identity while hostname mismatches remain errors.

## Run

Use the repository's pinned toolchain and an existing maintainer-built package
feed, then choose a fresh directory outside all implementation worktrees:

```sh
python3 tests/Verification.EndToEnd/api_probe.py \
  --feed /absolute/producer-feed \
  --output /absolute/b22-api-run \
  --postgres-bin /absolute/postgresql-18.0/bin
```

The controller creates a standalone consumer with no repository build imports,
an isolated NuGet cache, fixed package versions, and locked restore. It records
source, controller, package, native module, lock, resolved archive and executable
hashes before actual execution. JIT and AOT have separate outputs. Rust commands
are trapped; the machine itself is not claimed to be a pristine Rust-free host.
The controller starts PostgreSQL and the consumers in its own process tree.
PostgreSQL uses unchanged `initdb` durability defaults and explicitly local,
non-TLS database connections; the HTTP transport remains TLS.

`--providers sqlite` or `--modes jit` selects a partial matrix and leaves omitted
rows unaccepted. `--allow-unlocked-restore` is only for development lock generation
and can never report the public API matrix complete. Existing output directories
are rejected to preserve failure and interruption records. Run a changed fixture
in a new directory; never relabel an older binary's receipt.

## Actual oracles

The executable checks pre-admission COMMIT cancellation and confirmed rollback,
an actual failed transaction, PostgreSQL physical-session identity, early cursor
retirement, explicit nullable/composite-FK semantics, HTTP automatic completion,
redacted handler failure, retained chunks beyond EOF, old response guards,
immediate per-request/global budget failure, and Pending Close followed by a
Dispose join that cannot finish until the charged byte owner releases.
The predeclared case manifest prevents a truncated or empty successful receipt
from satisfying a row. A payload reservation failure after HTTP response headers
commit is observed as the documented stream abort. A separate bodyless response
must still finish while the global payload credits remain held.

The nullable query preserves the pinned SQL contract: `Equal(null)` follows
three-valued logic; `WhereIsNull` explicitly selects null columns. An optional
composite relationship excludes either partially null key. Independent SQL reads
after the consumer exits reconcile the rows, canceled transaction absence and
nullable/FK dataset. SQLite also undergoes integrity and foreign-key checks.

The optional protocol cases exercise real Tonic binary metadata (including empty
segments and invalid encoding), typed results over native database execution,
23 forbidden raw HTTP operations inside gRPC handlers, encoded-message limits,
the identity-only compression profile, MCP protocol negotiation, MCP admission
pressure and late cancellation, and SSE cancellation reaching the generator.
Identity-only rejection does not claim a successful decompression implementation.

Weak-reference observations cover only explicitly identified source/scope objects.
Six fixed native database counters are read from each actual loaded module, and
public Server close results retain pending requests and byte charges. None of
these observations is an aggregate count of all managed GCHandles or roots.
Independent B22 observer/fault/soak profiles own that wider resource evidence.

## Evidence limits

`progress.json`, command logs, per-row `receipt.json` and independent SQL reports
retain actual results. A timeout or missing receipt is failure, never a zero-owner
or successful-cancellation observation. The four short rows alone cannot accept
B22: actual external faults, crash recovery, the 30-minute/24-hour/72-hour profiles
and independent technical review remain separate requirements. Windows and B24's
pristine package/release gates remain outside this consumer's claims.
