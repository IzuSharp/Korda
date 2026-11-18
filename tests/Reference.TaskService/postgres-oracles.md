# B21 PostgreSQL adversarial oracles

`postgres_oracles.py` runs two scoped B21 acceptance cases against the actual
reference application. It is called by the ordinary external product runner:

```python
result = run_postgres_oracles(command, config, output, service_factory, sql)
```

`service_factory(config, output)` returns a context manager with
`request(method, path, body=None, headers=None, token=None)` returning
`(status, response_bytes, response_headers)`. Requests use separate bounded HTTP
clients so two requests can overlap. `sql(statement)` executes independent direct
`psql`, returning text/NULL tuples. The caller owns exact executable/native/package
identities, PostgreSQL version/configuration and the ordinary HTTP/TLS profile.

The input service configuration must select `provider: postgresql`, provide a
TCP PostgreSQL URI in `database`, and include a writable entry in `principals`
with `token`, `tenant`, `principal`, and `canWrite: true`. The test makes a private
copy and selects a loopback protocol proxy and
`allowLocalDatabaseTlsDisable: true`. The NativeWeave PostgreSQL provider rejects
URI query overrides, so the proxy must not append `sslmode=disable` to its URI.
This is a local database fault profile,
not a production TLS recommendation or an alteration of HTTP TLS acceptance.
Use the disposable database and data directories prepared by the product runner.

## Concurrent conflict snapshot

The first actual HTTP creation request inserts its operation/item/outbox rows.
The transparent proxy holds its COMMIT **before** sending it upstream. A second
HTTP request with the same key/hash issues its real conflicting INSERT. An
independent PostgreSQL observation must find the contender waiting on a database
lock held by the winner (`pg_blocking_pids` and an ungranted `pg_locks` entry).
This observed barrier, rather than a fixed sleep, releases the winner.

Both responses must be status 201 with byte-identical stable results. The actual
contender wire trace must show `INSERT 0 0` followed by a **separate next business**
operations SELECT completing `SELECT 1`. Preparing that typed lookup can first
load result-column origins through the pinned SQLx 0.9.0 driver's
`sqlx-postgres/src/connection/resolve.rs`, `ColumnResolver::push_column` and
`fill_cache`. Its native caller is `Session::describe_typed` in
`native/database/postgres/src/session.rs`, through SQLx `prepare_with`.

The oracle recognizes the complete fixed catalog query shape: it reads only
`pg_catalog.pg_attribute`, with at most eleven numeric table-OID/attribute pairs
and no variable SQL identifiers or expressions. It permits at most one such
query before the operations read, records its sequence and exact statement
SHA-256, and rejects every other intervening SQL command. It does not skip an
arbitrary SELECT, timeout setting or business-table query. The operations read
still receives a new ReadCommitted statement snapshot; the catalog query is
reported explicitly rather than claiming literal adjacency on the wire.
Independent SQL verifies exactly one
operation, item and outbox effect, the separately encoded D20 request hash,
tenant/principal scope, response version and exact durable replay bytes.

## Lost COMMIT acknowledgment

For a fresh actual HTTP creation request, the proxy forwards COMMIT unchanged,
then consumes and discards the server's `CommandComplete(COMMIT)` and idle
`ReadyForQuery`. It closes that client connection only after observing both.
The application must return HTTP 503 `outcome_unknown` with the original recovery
identity. Direct independent SQL must nevertheless see the durable effect.
Authorized recovery and protected same-key POST replay must return the original
stored response, with the same single item/outbox effect afterward.

The proxy targets only a transaction which executed an operations INSERT. It
does not fault startup migrations or the background outbox worker. PostgreSQL
authentication and query parameters pass unchanged; no product test hook or SQL
rewrite is introduced. The proxy handles simple and extended query messages,
and it does not record authentication payloads, bound values, complete SQL or
backend cancellation secrets.

`protocol.jsonl` is flushed for each observed event. `result.json` checkpoints
completed cases and preserves failures. A missing server lock witness, missing
COMMIT/idle witness, wrong outcome, wrong response or effect count fails the run;
an unavailable database is not a skipped success. These cases do not establish
B22's full fault matrix, power-loss durability or long-duration soak acceptance.
