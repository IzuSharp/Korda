# B21 external reference-service verification

This controller targets the D20 reference service through real HTTP and an
independent durable SQL connection. It imports no product implementation. The
immutable `golden-v1.json` and Python byte oracle independently exercise the
versioned length-prefixed UTF-8 request-hash format, including Unicode and field
boundary cases. Product `--golden-vectors` must match the frozen bytes.

Install the pinned controller requirements from `requirements.txt`. Supply an
actual package-based JIT command or published Linux NativeAOT executable:

```sh
python3 tests/Reference.TaskService/run.py \
  --output artifacts/b21/sqlite-jit-http1 \
  --provider sqlite --database /absolute/disposable/reference.sqlite \
  -- dotnet /absolute/package-consumer/Reference.TaskService.dll
```

For PostgreSQL, select `--provider postgresql`, a disposable database URI and
`--psql /absolute/postgresql/bin/psql`. The service and oracle need a database they
may initialize and modify. For TLS use `--tls-certificate` and `--tls-private-key`;
add `--http2` to require an actual HTTP/2 ALPN result rather than accepting a
fallback. The controller validates the certificate. PostgreSQL protocol-fault
oracles, when selected with `--postgres-oracles`, apply only to the isolated
loopback database transport, independently from HTTP TLS.

Each output directory is new and retains case progress, service logs, controller
and golden hashes, and a summary only after all selected cases pass. The temporary
`config.private.json` contains generated local test credentials and must not be
published as evidence. Logs do not include authentication headers or connection
strings. A failed run remains failed; it is not overwritten by a later pass.

The required B21 matrix covers SQLite/PostgreSQL, JIT/actual NativeAOT and native
HTTP1/HTTP2/TLS. Exact package/native/executable identities and ordinary C# edits
without Cargo are recorded by the package consumer runner. A controller source
file or successful Python syntax check is not an executed product row.

B22 owns the complete external fault matrix and the 30-minute, 24-hour and
72-hour resource profiles. B21's finite protocol/data cases do not satisfy those
long-duration obligations. Windows remains outside this Linux run.
