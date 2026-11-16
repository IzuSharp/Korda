# Typed stream component sample

This maintainer sample uses actual B02 PostgreSQL/B03 SQLite execution, the B06
bounded native cursor and the internal managed typed adapter. It demonstrates
static `Projection<T>`, owned DTO materialization, `await foreach` early break,
confirmed cleanup and a bounded `IReadOnlyList<T>` result.

The linked test-only transport is deliberately labelled. The sample is not an
installable public `DataSession` or B04 host and is not pristine NuGet-consumer
evidence. No new public C# transport/handle API is exposed. B04/B13 will compose
the designed source/session/query entrypoints with this adapter.

Run the module verifier from the repository root:

```sh
python3 tests/Data.Streaming/verify.py --postgres-bin /path/to/postgresql-18/bin
```

It builds the shim and runs this sample once per actual provider, alongside the
JIT and NativeAOT component suite. Source, native binary, sample assembly, command
and result hashes in that run identify its executed state; this file is not an
independent acceptance assertion. Read the [streaming guide](../../../docs/guides/data-streaming.md)
for ownership, supported limits, cancellation, allocation and remaining host/package scope.
