"""Source-bound contracts for the independent PostgreSQL Find SQL diagnostic."""
import hashlib
import json
from pathlib import Path

MODEL_REF = "456140129c3ef3ff1ea0412b2b8cb3da82d099fd"
WORKLOAD_SHA = "76443b1c88bf011f57b3c9819dc85a8bedb5376121d3ef2670304f96b6d2aae2"
BUNDLE_SHA = "5a2eba0796a009bfa316dfc07c66919c8b3e14f600faa808e96cd32da617885c"
LANES = ("original", "qualified", "limited")
FIXTURE = {"tenants": 8, "ordersPerTenant": 1024, "customers": 256, "requestedRows": 32, "batchRows": 32}


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def read_contract(path):
    if sha(path) != WORKLOAD_SHA:
        raise ValueError("Use the unchanged archived queries-v3 workload")
    manifest = json.loads(Path(path).read_text())
    if manifest["schemaVersion"] != 3 or manifest["dataset"] != FIXTURE:
        raise ValueError("The fixed dataset contract changed")
    query = next(q for q in manifest["queries"] if q["name"] == "tx-commit")
    full = query["sql"]["postgresql"]
    if not full.endswith(" LIMIT $3") or query["parameters"] != [
            {"name": "p1", "kind": "int64", "value": 1},
            {"name": "p2", "kind": "int64", "value": 1024},
            {"name": "p3", "kind": "int64", "value": 2}]:
        raise ValueError("The public Korda Find contract changed")
    original = query["lifecycle"]["independentOrmSql"]["FindSql"]
    original_wire = original.replace("@tenant", "$1").replace("@id", "$2")
    qualified_wire = full.removesuffix(" LIMIT $3")
    qualified = qualified_wire.replace("$1", "@p1").replace("$2", "@p2")
    limited = full.replace("$1", "@p1").replace("$2", "@p2").replace("$3", "@p3")
    def lane(name, sql, server, parameters):
        return {"name": name, "client_sql": sql, "expected_server_sql": server,
                "parameters": [{"ordinal": i+1, "name": n, "kind": "int64", "clr_type": "System.Int64", "value": v}
                               for i, (n, v) in enumerate(parameters)]}
    return {"workload_sha256": WORKLOAD_SHA, "dataset": FIXTURE,
            "initial_order": query["lifecycle"]["initial"], "columns": query["columns"],
            "lanes": [lane("original", original, original_wire, [("tenant", 1), ("id", 1024)]),
                      lane("qualified", qualified, qualified_wire, [("p1", 1), ("p2", 1024)]),
                      lane("limited", limited, full, [("p1", 1), ("p2", 1024), ("p3", 2)])],
            "scope": "Same Dapper.AOT QuerySingleAsync<Order> and Npgsql connection on all lanes. Named placeholders are rewritten by Npgsql; the separate server trace must equal expected_server_sql byte-for-byte. This does not replace original ORM results or the formal transaction workload."}


def extract_models(source):
    """Keep exact original type/handler bytes, without unrelated RunReceipt JSON roots."""
    chunks = []
    segments = []
    def add(name, start, end):
        data = source[start:end]
        chunks.append(data)
        segments.append({"name": name, "start_byte": start, "end_byte_exclusive": end,
                         "sha256": digest(data)})
    settings = source.index(b"public sealed record OrderSettings(")
    add("imports-and-module-handlers", 0, settings)
    add("OrderSettings", settings, source.index(b";\n", settings) + 2)
    order = source.index(b"public sealed record Order(")
    add("Order-nine-fields", order, source.index(b";\n", order) + 2)
    money = source.index(b"public sealed class MoneyHandler")
    settings_handler = source.index(b"public sealed class SettingsHandler")
    nullable = source.index(b"public sealed class NullableMoneyHandler")
    json_roots = source.index(b"[JsonSerializable", nullable)
    add("MoneyHandler", money, settings_handler)
    add("SettingsHandler", settings_handler, nullable)
    add("NullableMoneyHandler", nullable, json_roots)
    # The original project enables nullable annotations; *.generated.cs needs
    # that same context explicitly even when the project has Nullable=enable.
    wrapper = b"#nullable enable\n\n"
    result = wrapper + b"\n".join(chunks)
    if b"typeof(RunReceipt)" in result or b"typeof(LifecycleReceipt)" in result:
        raise ValueError("Unrelated measurement record dependency escaped extraction")
    return result, {"original_sha256": digest(source), "segments": segments,
                    "wrapper_prefix": wrapper.decode(),
                    "extracted_sha256": digest(result),
                    "scope": "Exact slices of the committed original Models.cs. Only unrelated model types and JSON context roots are omitted; PostgreSQL handlers are unchanged."}


def expected_row_digest(order):
    value = 14695981039346656037
    mask = (1 << 64) - 1
    def feed(data):
        nonlocal value
        for byte in data:
            value = ((value ^ byte) * 1099511628211) & mask
    def integer(number): feed(int(number).to_bytes(8, "little", signed=True))
    def text(value):
        if value is None:
            integer(-1)
        else:
            data = value.encode("utf-8")
            integer(len(data)); feed(data)
    for key in ("TenantId", "Id", "CreatedAt"): integer(order[key])
    feed(bytes([int(order["CustomerId"] is not None)]))
    if order["CustomerId"] is not None: integer(order["CustomerId"])
    integer(order["TotalCents"]); text(order["Note"]); integer(order["Version"])
    text(order["Settings"]["Channel"]); feed(bytes([int(order["Settings"]["Gift"])]))
    text(order["InternalMemo"])
    return value


def combined_digest(row_digest, count):
    value = 14695981039346656037
    for _ in range(count):
        value = ((value ^ row_digest) * 1099511628211) & ((1 << 64) - 1)
    return value
