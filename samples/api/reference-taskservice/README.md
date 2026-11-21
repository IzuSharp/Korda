# Direct database application sample

This sample links the finite internal application fixture in `src/Reference.TaskService`. It invokes strongly typed database business methods directly and creates no server or protocol listener. The default references only `NativeWeave.Database.Sqlite`; set `ReferenceIncludePostgreSql=true` to include the PostgreSQL facade as well.

The source demonstrates explicit migrations, static models and DTO mapping, typed CRUD and keyset queries, scoped idempotency, optimistic concurrency, transactions with uncertain-commit recovery, a separate durable outbox consumer, and bounded resource disposal. Filesystem attachment logic is internal application code used to verify database-reference safety, not an additional NativeWeave engine.

Use the current candidate version and feed through ordinary NuGet configuration:

```bash
dotnet run --project samples/api/reference-taskservice \
  -p:NativeWeavePackageVersion=0.1.0-db-scope.1.2 \
  -- --config /absolute/fixture.private.json --verify
```

Example SQLite configuration for a new empty database:

```json
{
  "provider": "sqlite",
  "database": "/absolute/new/application.sqlite",
  "blobDirectory": "/absolute/new/blobs",
  "consumerDatabase": "/absolute/new/consumer.sqlite",
  "cursorKey": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
  "principals": [{"tenant": "tenant-a", "principal": "alice", "canWrite": true}]
}
```

The fixed cursor key is only a reproducible local fixture. The finite `--verify` scenario checks actual data and exits after database cleanup. `--migrate-only`, `--outbox-once` and `--maintenance-once` are explicit finite operations. Package-based JIT/Native AOT acceptance runs outside the source tree through `tests/Reference.TaskService/package_probe.py`; it does not rely on this source-linked sample as proof of package independence.
