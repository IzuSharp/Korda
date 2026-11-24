#!/usr/bin/env python3
"""Bounded, non-timing SQL/parameter/plan evidence for DB-Perf-01.

PostgreSQL observes actual auto_explain and log_statement records. SQLite uses
an explicitly separate read-only EXPLAIN QUERY PLAN replay; it does not claim
to observe a provider's hidden statement cache or its dispatch-time plan.
"""
from __future__ import annotations

import ctypes as C
import hashlib
import json
from pathlib import Path
import re
from typing import Any

ROUTES = {"direct_cpp", "korda_before", "korda_after", "independent_csharp_orm"}
KINDS = {"int32", "int64", "string", "nullable-string"}


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _get(value: dict, name: str, default=None):
    matches = [item for key, item in value.items() if key.casefold() == name.casefold()]
    if len(matches) > 1:
        raise ValueError("Ambiguous case-folded receipt key: " + name)
    return matches[0] if matches else default


def _object(value: dict | str | Path) -> tuple[dict, dict]:
    if isinstance(value, dict):
        encoded = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
        return value, {"canonicalJsonSha256": hashlib.sha256(encoded).hexdigest()}
    path = Path(value).resolve()
    return json.loads(path.read_text()), {"path": str(path), "sha256": sha(path)}


def sql_tokens(sql: str) -> list[str]:
    """Tokenize, preserving quoted text and placeholder identity. No SQL rewriting.

    Bare words are case-insensitive; quoted identifiers and string bytes are
    retained. Semicolons inside literals/comments never become separators.
    """
    result: list[str] = []
    i = 0
    while i < len(sql):
        char = sql[i]
        if char.isspace():
            i += 1
            continue
        if sql.startswith("--", i):
            end = sql.find("\n", i)
            i = len(sql) if end < 0 else end + 1
            continue
        if sql.startswith("/*", i):
            depth, end = 1, i + 2
            while depth and end < len(sql):
                if sql.startswith("/*", end):
                    depth += 1
                    end += 2
                elif sql.startswith("*/", end):
                    depth -= 1
                    end += 2
                else:
                    end += 1
            if depth:
                raise ValueError("Unterminated SQL comment")
            i = end
            continue
        if char in "'\"`[":
            quote = "]" if char == "[" else char
            end = i + 1
            escaped = char == "'" and result and result[-1] == "e"
            while end < len(sql):
                if escaped and sql[end] == "\\":
                    end += 2
                elif sql[end] == quote:
                    if end + 1 < len(sql) and sql[end + 1] == quote:
                        end += 2
                    else:
                        end += 1
                        break
                else:
                    end += 1
            else:
                raise ValueError("Unterminated SQL quoted token")
            result.append(sql[i:end])
            i = end
            continue
        if char == "$":
            quoted = re.match(r"\$(?:[A-Za-z_][A-Za-z_0-9]*)?\$", sql[i:])
            if quoted:
                marker = quoted.group()
                end = sql.find(marker, i + len(marker))
                if end < 0:
                    raise ValueError("Unterminated SQL dollar quote")
                end += len(marker)
                result.append(sql[i:end])
                i = end
                continue
        matched = re.match(r"(?:\$[0-9]+|[?][0-9]*|[@:][A-Za-z_][A-Za-z_0-9]*|[A-Za-z_][A-Za-z_0-9$]*|[0-9]+(?:\.[0-9]+)?|::|<=|>=|<>|!=|\|\|)", sql[i:])
        if matched:
            token = matched.group()
            # :: is an operator, not a named parameter.
            result.append(token.lower() if token[0].isalpha() else token)
            i += len(token)
        else:
            result.append(char)
            i += 1
    while result and result[-1] == ";":
        result.pop()
    return result


def normalize_sql(sql: str) -> str:
    names: dict[str, int] = {}
    result = []
    anonymous = 0
    for token in sql_tokens(sql):
        if token == "?":
            anonymous += 1
            token = "$" + str(anonymous)
        elif re.fullmatch(r"[?][0-9]+", token):
            token = "$" + token[1:]
        elif re.fullmatch(r"[@:][A-Za-z_][A-Za-z_0-9]*", token):
            if token not in names:
                names[token] = len(names) + 1
            token = "$" + str(names[token])
        result.append(token)
    if ";" in result:
        raise ValueError("Multiple SQL statements are not one contract leg")
    return " ".join(result)


def parse_parameter_log(text: str | None) -> list[str | None]:
    """Parse PostgreSQL BuildParamLogString, including quoted NULL and commas."""
    if text is None or not text.strip():
        return []
    text = re.sub(r"^Parameters:\s*", "", text.strip(), flags=re.I)
    result: list[str | None] = []
    position = 0
    while position < len(text):
        matched = re.match(r"\$(\d+)\s*=\s*", text[position:])
        if not matched or int(matched[1]) != len(result) + 1:
            raise ValueError("Missing, duplicate or unordered PostgreSQL parameter ordinal")
        position += matched.end()
        if text.startswith("NULL", position):
            value = None
            position += 4
        elif position < len(text) and text[position] == "'":
            position += 1
            chars = []
            while position < len(text):
                if text[position] == "'":
                    if position + 1 < len(text) and text[position + 1] == "'":
                        chars.append("'")
                        position += 2
                    else:
                        position += 1
                        break
                else:
                    chars.append(text[position])
                    position += 1
            else:
                raise ValueError("Unterminated PostgreSQL parameter value")
            value = "".join(chars)
        else:
            raise ValueError("PostgreSQL parameter value is neither quoted nor NULL")
        result.append(value)
        if position != len(text):
            if text[position:position + 2] != ", ":
                raise ValueError("Truncated or malformed PostgreSQL parameter log")
            position += 2
            if position == len(text):
                raise ValueError("Trailing PostgreSQL parameter separator")
    return result


def _parameters(parameters: list[dict]) -> list[dict]:
    result = []
    for index, parameter in enumerate(parameters):
        kind, value = parameter["kind"], parameter["value"]
        if kind not in KINDS:
            raise ValueError("Unsupported typed parameter: " + kind)
        if kind in ("int32", "int64"):
            bits = 32 if kind == "int32" else 64
            if type(value) is not int or not -(1 << (bits - 1)) <= value < (1 << (bits - 1)):
                raise ValueError("Integer parameter outside declared width")
        elif not isinstance(value, str) and not (kind == "nullable-string" and value is None):
            raise ValueError("Invalid typed string parameter")
        result.append({"ordinal": index + 1, "name": parameter["name"], "kind": kind, "value": value})
    return result


def contract_legs(manifest: dict, provider: str, route: str) -> list[dict]:
    if provider not in ("sqlite", "postgresql") or route not in ROUTES:
        raise ValueError("Unknown diagnostic provider/route")
    legs = []
    names = [query["name"] for query in manifest["queries"]]
    if len(names) != len(set(names)) or not names:
        raise ValueError("Manifest query names must be unique and nonempty")
    for query in manifest["queries"]:
        lifecycle = query.get("lifecycle", {})
        steps = lifecycle.get("steps")
        if steps is None:
            items = [{"name": "query", "sql": query["sql"], "parameters": query["parameters"]}]
        else:
            if not steps or "begin" not in lifecycle:
                raise ValueError("Incomplete lifecycle contract: " + query["name"])
            items = [{"name": "begin", "sql": lifecycle["begin"], "parameters": []}, *steps]
        for item in items:
            text, parameters = item["sql"][provider], item["parameters"]
            provenance = "validated shared manifest SQL"
            if route == "independent_csharp_orm" and steps is not None:
                variants = lifecycle["independentOrmSql"]
                parameter_names = None
                if item["name"] == "begin" and provider == "postgresql":
                    # Npgsql ReadCommitted command, retained in the validated PG log.
                    text = "BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED"
                elif text.lstrip().upper().startswith("SELECT"):
                    text, parameter_names = variants["FindSql"], ["tenant", "id"]
                    parameters = parameters[:2]  # Public Korda Find adds LIMIT 2.
                elif text.lstrip().upper().startswith("UPDATE"):
                    if item["name"] == "stale-change":
                        key, parameter_names = "StaleSql", ["note", "tenant", "id", "expected"]
                    elif query["name"] == "partial-update":
                        key, parameter_names = "PartialSql", ["note", "tenant", "id"]
                    elif query["name"] == "stale-conflict":
                        key, parameter_names = "CheckedSql", ["note", "version", "tenant", "id", "expected"]
                    else:
                        key, parameter_names = "UpdateSql", ["note", "version", "tenant", "id"]
                    text = variants[key]
                if parameter_names is not None:
                    if len(parameters) != len(parameter_names):
                        raise ValueError("Independent ORM parameter mapping is incomplete")
                    parameters = [dict(parameter, name=name) for parameter, name in zip(parameters, parameter_names)]
                provenance = "manifest independentOrmSql plus validated Npgsql transaction command"
            normalized = normalize_sql(text)
            parameters = _parameters(parameters)
            ordinals = [int(token[1:]) for token in sql_tokens(normalized) if re.fullmatch(r"\$\d+", token)]
            if set(ordinals) != set(range(1, len(parameters) + 1)):
                raise ValueError("SQL placeholder set does not match typed parameters: " + query["name"] + "/" + item["name"])
            first = sql_tokens(text)[0]
            plannable = first in ("select", "with", "update", "insert", "delete", "merge")
            if not plannable and first not in ("begin", "commit", "rollback"):
                raise ValueError("Unsupported non-plannable lifecycle leg")
            legs.append({"caseId": query["name"], "legId": query["name"] + "/" + item["name"],
                         "sql": text, "normalizedSql": normalized, "parameters": parameters,
                         "requiresPlan": plannable, "sqlProvenance": provenance})
    return legs


def _signature(sql: str, parameters: list[str | None]) -> tuple:
    return normalize_sql(sql), tuple(parameters)


def _expected_values(leg: dict) -> list[str | None]:
    return [None if p["value"] is None else str(p["value"]) for p in leg["parameters"]]


def _coverage(manifest: dict, raw: dict) -> tuple[dict, list[str], list[str]]:
    expected = [query["name"] for query in manifest["queries"]]
    order = _get(raw, "actualCaseIds", _get(raw, "executedCaseIds"))
    errors = []
    if not isinstance(order, list) or len(order) != len(expected) or set(order) != set(expected):
        errors.append("Actual executed case IDs do not cover the complete manifest exactly once")
        order = expected
    cases = _get(raw, "caseCounts", _get(raw, "cases", {}))
    if isinstance(cases, list):
        pairs = [(_get(item, "name"), item) for item in cases]
        if len({key for key, _ in pairs}) != len(pairs):
            errors.append("Duplicate per-case receipts")
        cases = dict(pairs)
    if not isinstance(cases, dict) or set(cases) != set(expected):
        errors.append("Per-case counters do not cover the complete manifest")
        cases = cases if isinstance(cases, dict) else {}
    normalized = {}
    for query in manifest["queries"]:
        name = query["name"]
        item = cases.get(name, {})
        started, succeeded, failed, rows = (_get(item, key) for key in ("started", "succeeded", "failed", "rows"))
        if (type(started) is not int or type(succeeded) is not int or type(failed) is not int or type(rows) is not int
                or succeeded <= 0 or failed != 0 or started != succeeded or rows != succeeded * query["expectedRows"]):
            errors.append("Invalid successful-operation/row coverage for " + name)
        normalized[name] = {"started": started, "succeeded": succeeded, "failed": failed, "rows": rows}
    # Native Completed includes failures; managed Completed means successes.
    # This diagnostic requires zero failures, so both must equal Succeeded.
    top = {key: _get(raw, key) for key in ("started", "completed", "failed", "rows")}
    top["succeeded"] = _get(raw, "succeeded", top["completed"])
    if (any(type(value) is not int or value < 0 for value in top.values())
            or top["failed"] != 0 or top["started"] != top["succeeded"] or top["completed"] != top["succeeded"]):
        errors.append("Top-level diagnostic counters are missing, inconsistent, or contain failures")
    for key in ("started", "succeeded", "failed", "rows"):
        values = [item[key] for item in normalized.values()]
        if all(type(value) is int for value in values) and sum(values) != top[key]:
            errors.append("Per-case " + key + " does not reconcile with the top-level counter")
    if _get(raw, "concurrency") != 1 or _get(raw, "emptyDriver") is not False:
        errors.append("Plan diagnostics require a real-database single-worker consumer")
    return normalized, order, errors


def _base(manifest, route, raw, provider) -> tuple[dict, dict, dict]:
    document, manifest_identity = _object(manifest)
    consumer, raw_identity = _object(raw)
    cases, order, errors = _coverage(document, consumer)
    result = {"schemaVersion": 1, "task": "DB-Perf-01", "diagnostic": "query-plans", "provider": provider,
              "route": route, "manifestIdentity": manifest_identity, "consumerIdentity": raw_identity,
              "timingComparisonEligible": False, "performanceAcceptance": False,
              "caseCounts": cases, "executedCaseOrder": order, "errors": errors, "passed": False}
    return document, consumer, result


def _consumer_sql(consumer: dict, legs: list[dict]) -> tuple[list[dict], list[str]]:
    texts = _get(consumer, "actualDiagnosticSql", _get(consumer, "actualSql", {}))
    checks, errors = [], []
    for leg in legs:
        if not leg["legId"].endswith("/query"):
            continue
        text = texts.get(leg["caseId"]) if isinstance(texts, dict) else None
        item = {"caseId": leg["caseId"], "consumerText": text,
                "scope": "Consumer-reported SQL text is construction metadata, not an independent dispatch trace"}
        if not isinstance(text, str) or not re.match(r"^\s*(SELECT|WITH)\b", text, re.I):
            item["status"] = "not exposed as executable SQL; manifest SQL and separate server/replay evidence retained"
        else:
            item["matchesContract"] = normalize_sql(text) == leg["normalizedSql"]
            if not item["matchesContract"]:
                errors.append("Consumer-reported SQL differs from the contract for " + leg["caseId"])
        checks.append(item)
    return checks, errors


def collect_postgres(logPath, manifest, route, raw) -> dict:
    """Retain actual outer jsonlog entries and verify every business SQL leg.

    Plan/statement occurrences are counted by SQL+ordered values, with summed
    demands for identical legs: one logged lookup cannot credit three lookups.
    Logs contain no application case tag; that attribution limit is explicit.
    """
    document, consumer, result = _base(manifest, route, raw, "postgresql")
    result.update({"scope": "Actual server execution trace, including warmup and measured operations; no timing credit",
                   "serverParameterOids": None, "serverParameterOidStatus": "not emitted by auto_explain or log_statement; manifest kinds are expected client types",
                   "genericCustomPlanCounts": None, "genericCustomPlanCountStatus": "not instrumented in the executing connection; no inference from SQL or reuse flags",
                   "attribution": "SQL plus ordered parameter values and aggregate occurrence demand; no application case tags in server logs",
                   "outerJsonLog": [], "plans": [], "statements": [], "legs": [], "signatureCoverage": []})
    path = Path(logPath)
    try:
        result["logIdentity"] = {"path": str(path.resolve()), "sha256": sha(path), "bytes": path.stat().st_size}
        lines = path.read_text().splitlines()
        legs = contract_legs(document, "postgresql", route)
        result["consumerSqlChecks"], text_errors = _consumer_sql(consumer, legs)
        result["errors"].extend(text_errors)
    except (OSError, ValueError, KeyError, TypeError) as error:
        result["errors"].append(str(error))
        return result
    statements: dict[tuple, list[int]] = {}
    plans: dict[tuple, list[int]] = {}
    result["unparsedLines"] = []
    for line_number, line in enumerate(lines, 1):
        try:
            outer = json.loads(line)
            if not isinstance(outer, dict):
                raise ValueError("Outer PostgreSQL jsonlog entry must be an object")
            result["outerJsonLog"].append({"line": line_number, "record": outer})
            message = outer.get("message", "")
            if not isinstance(message, str):
                raise ValueError("PostgreSQL log message is not text")
            matched = re.match(r"^(?:statement: |execute [^:]+: )(.*)$", message, re.S)
            if matched:
                text = matched[1]
                detail = outer.get("detail")
                values = parse_parameter_log(detail) if detail and re.match(r"Parameters:", detail, re.I) else []
                entry = {"line": line_number, "sql": text, "parameters": values,
                         "pid": outer.get("pid"), "sessionId": outer.get("session_id"), "detail": detail}
                try:
                    signature = _signature(text, values)
                    entry["normalizedSql"] = signature[0]
                    statements.setdefault(signature, []).append(len(result["statements"]))
                except ValueError as error:
                    entry["unmatchedReason"] = str(error)
                result["statements"].append(entry)
            marker = re.match(r"^duration: [0-9.]+ ms\s+plan:\s*", message)
            if marker:
                emitted = json.loads(message[marker.end():])
                emitted = emitted if isinstance(emitted, list) else [emitted]
                for plan in emitted:
                    if not isinstance(plan, dict) or not isinstance(plan.get("Query Text"), str) or not isinstance(plan.get("Plan"), dict):
                        raise ValueError("Malformed actual auto_explain plan")
                    values = parse_parameter_log(plan.get("Query Parameters"))
                    signature = _signature(plan["Query Text"], values)
                    plans.setdefault(signature, []).append(len(result["plans"]))
                    result["plans"].append({"line": line_number, "pid": outer.get("pid"),
                        "sessionId": outer.get("session_id"), "normalizedSql": signature[0], "parameters": values,
                        "autoExplain": plan})
        except (ValueError, KeyError, TypeError) as error:
            result["errors"].append("jsonlog line " + str(line_number) + ": " + str(error))
            result["unparsedLines"].append({"line": line_number, "text": line, "error": str(error)})
    if not result["outerJsonLog"]:
        result["errors"].append("Empty PostgreSQL jsonlog")
    warmup = _get(consumer, "warmupPerWorker")
    if type(warmup) is not int or warmup < 0:
        result["errors"].append("Missing exact warmup operation count")
        warmup = 0
    order = result["executedCaseOrder"]
    occurrences = {name: warmup // len(order) + (index < warmup % len(order))
                   + (result["caseCounts"][name]["succeeded"] or 0) for index, name in enumerate(order)}
    groups = {}
    for leg in legs:
        signature = _signature(leg["sql"], _expected_values(leg))
        group = groups.setdefault(signature, {"normalizedSql": signature[0], "parameters": list(signature[1]),
            "requiredStatements": 0, "requiredPlans": 0, "legIds": []})
        required = occurrences[leg["caseId"]]
        group["requiredStatements"] += required
        group["requiredPlans"] += required if leg["requiresPlan"] else 0
        group["legIds"].append(leg["legId"])
        result["legs"].append(dict(leg, expectedExecutionsIncludingWarmup=required,
            statementIndexes=statements.get(signature, []), planIndexes=plans.get(signature, [])))
    for signature, group in groups.items():
        group["observedStatements"] = len(statements.get(signature, []))
        group["observedPlans"] = len(plans.get(signature, []))
        group["passed"] = (group["observedStatements"] >= group["requiredStatements"]
                           and group["observedPlans"] >= group["requiredPlans"])
        result["signatureCoverage"].append(group)
        if not group["passed"]:
            result["errors"].append("Missing SQL/parameters/plan occurrences for " + ", ".join(group["legIds"]))
    result["unmatchedStatementIndexes"] = [index for signature, indexes in statements.items() if signature not in groups for index in indexes]
    result["unmatchedPlanIndexes"] = [index for signature, indexes in plans.items() if signature not in groups for index in indexes]
    # These extras include real framework health/transaction controls and native
    # EXPLAIN diagnostics. Preserve them; never silently count them as business SQL.
    result["completeCaseCount"] = len(document["queries"])
    result["completeLegCount"] = len(legs)
    result["passed"] = not result["errors"]
    return result


class _Sqlite:
    def __init__(self, library: Path, database: Path):
        self.lib = C.CDLL(str(library))
        self.db = C.c_void_p()
        specs = {
            "sqlite3_libversion": (C.c_char_p, []), "sqlite3_sourceid": (C.c_char_p, []),
            "sqlite3_compileoption_get": (C.c_char_p, [C.c_int]),
            "sqlite3_open_v2": (C.c_int, [C.c_char_p, C.POINTER(C.c_void_p), C.c_int, C.c_char_p]),
            "sqlite3_close": (C.c_int, [C.c_void_p]), "sqlite3_errmsg": (C.c_char_p, [C.c_void_p]),
            "sqlite3_prepare_v2": (C.c_int, [C.c_void_p, C.c_char_p, C.c_int, C.POINTER(C.c_void_p), C.POINTER(C.c_char_p)]),
            "sqlite3_bind_parameter_count": (C.c_int, [C.c_void_p]),
            "sqlite3_bind_int": (C.c_int, [C.c_void_p, C.c_int, C.c_int]),
            "sqlite3_bind_int64": (C.c_int, [C.c_void_p, C.c_int, C.c_int64]),
            "sqlite3_bind_null": (C.c_int, [C.c_void_p, C.c_int]),
            "sqlite3_bind_text": (C.c_int, [C.c_void_p, C.c_int, C.c_char_p, C.c_int, C.c_void_p]),
            "sqlite3_step": (C.c_int, [C.c_void_p]), "sqlite3_finalize": (C.c_int, [C.c_void_p]),
            "sqlite3_column_count": (C.c_int, [C.c_void_p]),
            "sqlite3_column_text": (C.c_void_p, [C.c_void_p, C.c_int]),
            "sqlite3_column_bytes": (C.c_int, [C.c_void_p, C.c_int]),
            "sqlite3_stmt_isexplain": (C.c_int, [C.c_void_p]),
            "sqlite3_total_changes64": (C.c_int64, [C.c_void_p]),
            "sqlite3_get_autocommit": (C.c_int, [C.c_void_p])}
        for name, (restype, argtypes) in specs.items():
            method = getattr(self.lib, name)
            method.restype, method.argtypes = restype, argtypes
        # SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX. Never execute business SQL.
        status = self.lib.sqlite3_open_v2(str(database).encode(), C.byref(self.db), 0x1 | 0x10000, None)
        if status:
            message = self.message(status)
            if self.db:
                self.lib.sqlite3_close(self.db)
                self.db = C.c_void_p()
            raise ValueError(message)

    def message(self, status: int) -> str:
        return "SQLite " + str(status) + ": " + self.lib.sqlite3_errmsg(self.db).decode("utf-8", "replace")

    def query(self, sql: str, parameters: list[dict] | None = None, explain=False) -> list[list[str | None]]:
        parameters = parameters or []
        statement, tail = C.c_void_p(), C.c_char_p()
        encoded = sql.encode()
        status = self.lib.sqlite3_prepare_v2(self.db, encoded, len(encoded) + 1, C.byref(statement), C.byref(tail))
        if status:
            raise ValueError(self.message(status))
        try:
            if not statement or (tail.value or b"").strip():
                raise ValueError("SQLite replay did not prepare exactly one statement")
            if explain and self.lib.sqlite3_stmt_isexplain(statement) != 2:
                raise ValueError("SQLite replay was not EXPLAIN QUERY PLAN")
            if self.lib.sqlite3_bind_parameter_count(statement) != len(parameters):
                raise ValueError("SQLite replay placeholder count differs from typed bindings")
            for index, p in enumerate(parameters, 1):
                if p["value"] is None:
                    status = self.lib.sqlite3_bind_null(statement, index)
                elif p["kind"] == "int32":
                    status = self.lib.sqlite3_bind_int(statement, index, p["value"])
                elif p["kind"] == "int64":
                    status = self.lib.sqlite3_bind_int64(statement, index, p["value"])
                else:
                    value = p["value"].encode()
                    status = self.lib.sqlite3_bind_text(statement, index, value, len(value), C.c_void_p(-1))
                if status:
                    raise ValueError(self.message(status))
            rows = []
            while True:
                status = self.lib.sqlite3_step(statement)
                if status == 101:
                    break
                if status != 100:
                    raise ValueError(self.message(status))
                row = []
                for column in range(self.lib.sqlite3_column_count(statement)):
                    pointer = self.lib.sqlite3_column_text(statement, column)
                    row.append(None if not pointer else C.string_at(pointer, self.lib.sqlite3_column_bytes(statement, column)).decode())
                rows.append(row)
            return rows
        finally:
            self.lib.sqlite3_finalize(statement)

    def close(self):
        if self.db:
            status = self.lib.sqlite3_close(self.db)
            if status:
                raise ValueError(self.message(status))
            self.db = C.c_void_p()


def _database_files(path: Path) -> dict:
    return {str(candidate): {"sha256": sha(candidate), "bytes": candidate.stat().st_size}
            for candidate in (path, Path(str(path) + "-wal")) if candidate.is_file()}


def replay_sqlite(library, db, manifest, route, raw) -> dict:
    document, consumer, result = _base(manifest, route, raw, "sqlite")
    result.update({"scope": "Separate read-only EXPLAIN QUERY PLAN replay from bound manifest SQL/values; not provider dispatch or hidden prepared-cache observation",
                   "actualDispatchTrace": False, "transactionControlExecution": False, "legs": [],
                   "bindingScope": "sqlite3_bind_int for int32, sqlite3_bind_int64 for int64, UTF-8 text with explicit length/SQLITE_TRANSIENT, sqlite3_bind_null for NULL"})
    connection = None
    library, database = Path(library).resolve(), Path(db).resolve()
    try:
        result["library"] = {"path": str(library), "sha256": sha(library)}
        result["databaseBefore"] = _database_files(database)
        if not database.is_file():
            raise ValueError("SQLite replay database is absent")
        legs = contract_legs(document, "sqlite", route)
        result["consumerSqlChecks"], text_errors = _consumer_sql(consumer, legs)
        result["errors"].extend(text_errors)
        connection = _Sqlite(library, database)
        result["sqliteVersion"] = connection.lib.sqlite3_libversion().decode()
        result["sqliteSourceId"] = connection.lib.sqlite3_sourceid().decode()
        options = []
        index = 0
        while value := connection.lib.sqlite3_compileoption_get(index):
            options.append(value.decode())
            index += 1
        result["compileOptions"] = options
        for option in ("foreign_keys=ON", "synchronous=FULL", "busy_timeout=5000", "query_only=ON"):
            connection.query("PRAGMA " + option)
        result["connectionOptions"] = {name: connection.query("PRAGMA " + name) for name in
            ("encoding", "journal_mode", "synchronous", "foreign_keys", "busy_timeout", "query_only", "cache_size", "temp_store", "mmap_size")}
        for leg in legs:
            entry = dict(leg, passed=False)
            try:
                if not leg["requiresPlan"]:
                    entry.update({"passed": True, "plan": None,
                        "planStatus": "Transaction control has no query plan; retained as a contract leg and never executed in replay"})
                else:
                    rows = connection.query("EXPLAIN QUERY PLAN " + leg["sql"], leg["parameters"], explain=True)
                    if not rows or any(len(row) != 4 for row in rows):
                        raise ValueError("SQLite did not emit the expected four-column plan")
                    entry.update({"passed": True, "plan": [dict(zip(("id", "parent", "notUsed", "detail"), row)) for row in rows]})
            except (ValueError, OSError) as error:
                entry["error"] = str(error)
                result["errors"].append(leg["legId"] + ": " + str(error))
            result["legs"].append(entry)
        result["totalChanges"] = connection.lib.sqlite3_total_changes64(connection.db)
        result["autocommitAfter"] = bool(connection.lib.sqlite3_get_autocommit(connection.db))
        if result["totalChanges"] != 0 or not result["autocommitAfter"]:
            result["errors"].append("Read-only plan replay changed data or left a transaction")
        result["completeCaseCount"] = len(document["queries"])
        result["completeLegCount"] = len(legs)
    except (ValueError, KeyError, TypeError, OSError, AttributeError) as error:
        result["errors"].append(str(error))
    finally:
        if connection is not None:
            try:
                connection.close()
            except ValueError as error:
                result["errors"].append(str(error))
    result["databaseAfter"] = _database_files(database)
    result["databaseBytesUnchanged"] = result.get("databaseBefore") == result["databaseAfter"]
    # SQLite may create an empty WAL sidecar even for a READONLY connection.
    # Keep that exact file-set difference visible; it is not changed database
    # content. A nonempty WAL appearing, disappearing or changing still fails.
    before = result.get("databaseBefore", {})
    after = result["databaseAfter"]
    wal = str(database) + "-wal"
    result["emptyWalFileSetChange"] = ({"before": before.get(wal), "after": after.get(wal)}
        if before.get(wal) != after.get(wal) and all(value is None or value["bytes"] == 0 for value in (before.get(wal), after.get(wal))) else None)
    content_before = {name: value for name, value in before.items() if name != wal or value["bytes"] != 0}
    content_after = {name: value for name, value in after.items() if name != wal or value["bytes"] != 0}
    result["databaseContentUnchanged"] = str(database) in content_before and content_before == content_after
    result["libraryUnchanged"] = library.is_file() and result.get("library", {}).get("sha256") == sha(library)
    if not result["databaseContentUnchanged"] or not result["libraryUnchanged"]:
        result["errors"].append("Replay database or loaded engine bytes changed")
    result["passed"] = not result["errors"]
    return result
