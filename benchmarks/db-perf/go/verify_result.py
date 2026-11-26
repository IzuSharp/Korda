#!/usr/bin/env python3
"""Independently audit existing Go DB-Perf-01 evidence; never execute a workload.

Requires the result, its external command receipt, the frozen workload manifest,
and the recorded local plan (defaults to plan.json beside the command receipt).
A five-second preflight can pass correctness accounting but never final credit.
"""
import argparse
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
import sys

MANIFEST_SHA = "76443b1c88bf011f57b3c9819dc85a8bedb5376121d3ef2670304f96b6d2aae2"
SQLITE_SHA = "2f693e43707c8174c862283185610abca5c671bbf17b6b1af7866bfbcd290282"
RUNNER_SHA = "296e6b802d8bc1bcf964d7faa71d4adaa37226a867c17a9a3b5f00284f769b86"
REFERENCE_SHA = {
    "sqlite": "2cc385eebeb2d02b23dee2dd29559a667ea2723051967928ac8bd24a45684718",
    "postgresql": "c5568d3fb78b7c11c888dcf0d458ec2559d23862d764fe27aa14e43dba780bc2",
}
POSTGRES_TOOLS = {
    "postgres": "69284e8fffe7241b1d3cab6a3c58b118976823a3be3b22d729ecc9ab57462c13",
    "initdb": "9add7cdb31e50ca61ba47bb2cb7ae95508390a58b40b293c5894403c2121f9e3",
    "pg_isready": "bf3a4582fc098ea79eb2d1ab2b8f435d72a4c5dff86b8c574cd504b217b1dfe8",
    "psql": "fd530740559fc18dffdc6b8f3b8083831ce5a45979ab7ed68c17cae5d9794049",
}
CASE_NAMES = "numeric,text,money,json,wide,optional-filter,range,left-join,inner-join,null-notes,literal-note,customer-totals,product-totals,empty-sum,cursor,offset,exists,in-subquery,cte,window,union-all,union,intersect,except,running-total,three-join,anti-association,nullable-order,raw-sql,related,tx-commit,tx-rollback,partial-update,stale-conflict,early-stop".split(",")


class InvalidEvidence(ValueError):
    pass


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise InvalidEvidence(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def reject_constant(value):
    raise InvalidEvidence(f"non-finite JSON literal: {value}")


def read_json(path):
    with Path(path).open(encoding="utf-8") as source:
        return json.load(source, object_pairs_hook=unique_object, parse_constant=reject_constant)


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def identity(path):
    path = Path(path).resolve(strict=True)
    return {"path": str(path), "sha256": sha256(path)}


def at(value, path):
    for key in path.split("."):
        if not isinstance(value, dict) or key not in value:
            raise InvalidEvidence(f"missing required field: {path}")
        value = value[key]
    return value


def integer(value, path, minimum=0):
    result = at(value, path)
    if type(result) is not int or result < minimum:
        raise InvalidEvidence(f"{path} must be an integer >= {minimum}")
    return result


def number(value, path, minimum=0):
    result = at(value, path)
    if type(result) not in (int, float) or not math.isfinite(result) or result < minimum:
        raise InvalidEvidence(f"{path} must be a finite number >= {minimum}")
    return result


def utc(value):
    if not isinstance(value, str):
        raise InvalidEvidence("UTC boundary must be a string")
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise InvalidEvidence("UTC boundary is missing its timezone")
    return parsed.astimezone(dt.timezone.utc)


def empty(value):
    return value is None or value == [] or value == {}


def near(left, right):
    return math.isclose(left, right, rel_tol=1e-10, abs_tol=1e-8)


def brief(value):
    if isinstance(value, list) and len(value) > 48:
        encoded = json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode()
        result = {"length": len(value), "sha256": hashlib.sha256(encoded).hexdigest()}
        if all(type(item) is int for item in value):
            result.update(sum=sum(value), minimum=min(value), maximum=max(value))
        return result
    return value


class Audit:
    def __init__(self):
        self.checks = []
        self.inputs = {}
        self.metrics = {}
        self.derived = {}

    def check(self, name, condition, observed=None, expected=None):
        item = {"name": name, "passed": bool(condition)}
        if observed is not None:
            item["observed"] = brief(observed)
        if expected is not None:
            item["expected"] = brief(expected)
        self.checks.append(item)
        return bool(condition)

    def equal(self, name, observed, expected):
        return self.check(name, observed == expected, observed, expected)

    def require(self, name, condition, observed=None, expected=None):
        if not self.check(name, condition, observed, expected):
            raise InvalidEvidence(name)

    def input_file(self, name, path, expected=None):
        item = identity(path)
        self.inputs[name] = item
        if expected is not None:
            self.equal(name + " SHA-256", item["sha256"], expected)
        return item


def parse_command(argv):
    if not isinstance(argv, list) or not all(isinstance(x, str) for x in argv):
        raise InvalidEvidence("command argv is not a string array")
    if len(argv) < 8 or Path(argv[0]).name != "taskset" or argv[1:3] != ["-c", "0-3"] or Path(argv[3]).name != "env":
        raise InvalidEvidence("command must pin the Go client to CPUs0-3 and explicitly set runtime environment")
    index, environment = 4, {}
    while index < len(argv) and "=" in argv[index] and not argv[index].startswith("--"):
        key, value = argv[index].split("=", 1)
        if key in environment:
            raise InvalidEvidence("duplicate runtime environment setting")
        environment[key] = value
        index += 1
    executable = argv[index]
    index += 1
    flags = {}
    while index < len(argv):
        key = argv[index]
        if not key.startswith("--") or key in flags or index + 1 >= len(argv):
            raise InvalidEvidence("malformed or duplicate command flag")
        flags[key] = argv[index + 1]
        index += 2
    return executable, environment, flags


def histogram(value, name):
    bins = at(value, name)
    if not isinstance(bins, list) or len(bins) != 8192 or any(type(n) is not int or n < 0 for n in bins):
        raise InvalidEvidence(name + " must contain8192 nonnegative integer bins")
    return bins


def quantiles(bins):
    total = sum(bins)
    result = {}
    for label, q in (("P50", .50), ("P95", .95), ("P99", .99)):
        rank, cumulative, upper = math.ceil(total * q), 0, 0.0
        if total:
            for index, count in enumerate(bins):
                cumulative += count
                if cumulative >= rank:
                    upper = math.ldexp(1.0, index // 128) * (1 + (index % 128 + 1) / 128) / 1000
                    break
        result[label] = upper
    return result


def check_identity(a, result, command, plan, manifest, args, provider, seconds, rate, concurrency, idle):
    a.equal("result schema", at(result, "SchemaVersion"), 1)
    a.equal("result route", at(result, "Route"), "independent-go-database-sql")
    a.equal("frozen manifest identity", a.inputs["manifest"]["sha256"], MANIFEST_SHA)
    a.equal("result manifest identity", at(result, "ManifestSha256"), MANIFEST_SHA)
    a.equal("ordered manifest cases", [q["name"] for q in manifest["queries"]], CASE_NAMES)
    a.equal("ordered actual cases", at(result, "ActualCaseIds"), CASE_NAMES)
    a.equal("manifest dataset", manifest["dataset"], {"tenants": 8, "ordersPerTenant": 1024, "customers": 256, "requestedRows": 32, "batchRows": 32})
    for key, expected in {"Provider": provider, "Mode": "prepared", "SessionMode": "persistent", "Concurrency": concurrency, "WarmupPerWorker": 64,
                          "RequestedRows": 32, "BatchRows": 32, "Tenants": 8, "OrdersPerTenant": 1024, "GOMAXPROCS": 4,
                          "VerifyOnly": False, "Runtime": "go1.27.4"}.items():
        a.equal("result " + key, at(result, key), expected)
    a.equal("build Go version", at(result, "BuildInfo.GoVersion"), at(result, "Runtime"))
    dependencies = at(result, "BuildInfo.Deps")
    a.require("build dependencies are an array", isinstance(dependencies, list))
    dep_map = {d["Path"]: d for d in dependencies}
    expected_driver = "github.com/mattn/go-sqlite3" if provider == "sqlite" else "github.com/jackc/pgx/v5"
    other_driver = "github.com/jackc/pgx/v5" if provider == "sqlite" else "github.com/mattn/go-sqlite3"
    versions = {"github.com/jackc/pgx/v5": "v5.11.0", "github.com/mattn/go-sqlite3": "v1.14.53"}
    for path in (expected_driver, other_driver):
        if path == expected_driver or path in dep_map:
            a.equal("driver " + path, dep_map.get(path, {}).get("Version"), versions[path])
            a.check("no driver replacement " + path, not dep_map.get(path, {}).get("Replace"))
    settings = {s["Key"]: s["Value"] for s in at(result, "BuildInfo.Settings")}
    for key, expected in {"GOOS": "linux", "GOARCH": "amd64", "-buildmode": "exe"}.items():
        a.equal("build " + key, settings.get(key), expected)
    tags = set(settings.get("-tags", "").replace(",", " ").split())
    joint_prototype = other_driver in dep_map
    if joint_prototype:
        a.equal("joint-driver prototype is preflight only", args.kind, "preflight")
        a.equal("joint-driver prototype build tags", sorted(tags), ["libsqlite3"])
        a.derived["binaryDriverScope"] = "historical joint-driver preparation binary; never final30-minute credit"
    else:
        expected_tags = {"sqlite", "libsqlite3"} if provider == "sqlite" else {"postgresql"}
        a.equal("single-provider build tags", sorted(tags), sorted(expected_tags))
        a.check("opposite database driver absent", other_driver not in dep_map)
        a.derived["binaryDriverScope"] = "single-provider " + provider
    if provider == "sqlite":
        a.equal("SQLite cgo enabled", settings.get("CGO_ENABLED"), "1")
    else:
        a.check("PostgreSQL cgo build setting explicit", settings.get("CGO_ENABLED") in ("0", "1"), settings.get("CGO_ENABLED"))
    if "CompiledProvider" in result:
        a.equal("compiled provider identity", result["CompiledProvider"], provider)
    a.equal("clean compiled source revision", settings.get("vcs.modified"), "false")
    a.derived["compiledSourceCommit"] = settings.get("vcs.revision")

    executable, environment, flags = parse_command(at(command, "argv"))
    a.equal("explicit Go runtime environment", environment, {"GOGC": "100", "GOMEMLIMIT": "off", "GODEBUG": ""})
    required_flags = {"--provider": provider, "--concurrency": str(concurrency), "--gomaxprocs": "4", "--warmup": "64", "--queue-capacity": "64",
                      "--seconds": str(seconds), "--rate": str(rate), "--idle-seconds": str(idle)}
    for key, expected in required_flags.items():
        a.equal("command " + key, flags.get(key), expected)
    a.equal("command output path", str(Path(flags["--output"]).resolve()), str(args.result.resolve()))
    a.input_file("commandManifest", flags["--manifest"], MANIFEST_SHA)
    expected_executable = at(plan, "inputs.consumer.sha256")
    a.input_file("executable", executable, expected_executable)
    a.equal("result executable SHA-256", at(result, "ExecutableSha256"), expected_executable)
    a.equal("plan executable path", str(Path(at(plan, "inputs.consumer.path")).resolve()), str(Path(executable).resolve()))
    a.equal("plan provider", at(plan, "provider"), provider)
    a.equal("plan lane", at(plan, "lane"), "go-" + args.kind)
    a.equal("plan affinity", at(plan, "affinity"), {"client": "0-3", "postgres": "4-7", "observer": "8"})
    a.equal("reference plan SHA-256", at(plan, "inputs.referencePlan.sha256"), REFERENCE_SHA[provider])
    a.input_file("referencePlan", at(plan, "inputs.referencePlan.path"), REFERENCE_SHA[provider])
    a.equal("plan manifest SHA-256", at(plan, "inputs.manifest.sha256"), MANIFEST_SHA)
    a.equal("plan runner SHA-256", at(plan, "inputs.runner.sha256"), RUNNER_SHA)
    a.input_file("runnerSource", at(plan, "inputs.runner.path"), RUNNER_SHA)
    plan_commands = at(plan, "commands")
    a.require("exact fixture plus Go plan", isinstance(plan_commands, list) and len(plan_commands) == 2)
    fixture, target = plan_commands
    a.equal("fixture phase", fixture.get("phase"), "fixture")
    fixture_argv = fixture.get("argv", [])
    a.check("fixture is initialize operations0 only", "--initialize" in fixture_argv and "--operations" in fixture_argv and fixture_argv[fixture_argv.index("--operations")+1] == "0")
    a.equal("only new measured cohort is Go", target.get("cohort"), "go")
    a.equal("measured plan phase", target.get("phase"), args.kind)
    a.equal("command phase", at(command, "phase"), args.kind)
    a.equal("command name", at(command, "name"), target.get("name"))
    a.equal("client affinity in plan", target.get("cpus"), "0-3")
    a.equal("command timeout", number(command, "timeoutSeconds"), 2115 if args.kind == "final" else 120)
    a.equal("plan timeout", target.get("timeoutSeconds"), at(command, "timeoutSeconds"))
    planned_argv = [v.replace("{connection}", flags["--connection"]).replace("{output}", str(args.result.parent.resolve())) for v in target.get("argv", [])]
    a.equal("external command matches frozen plan", at(command, "argv"), ["taskset", "-c", "0-3", *planned_argv])

    runner_path = args.command.parent / "runner.json"
    a.input_file("runnerReceipt", runner_path)
    runner = read_json(runner_path)
    a.equal("actual observer affinity", at(runner, "cpuAffinity"), [8])
    a.equal("observed runner SHA-256", at(runner, "runnerSha256"), RUNNER_SHA)
    a.equal("observed plan SHA-256", at(runner, "planSha256"), a.inputs["plan"]["sha256"])
    database = at(result, "Database")
    for key, value in {"Provider": provider, "Tenants": 8, "OrdersPerTenant": 1024, "PersistentConnectionsPerWorker": 1}.items():
        a.equal("database " + key, at(database, key), value)
    if provider == "sqlite":
        for key, expected in {"DatabaseVersion": "3.51.3", "JournalMode": "wal", "Synchronous": 2, "ForeignKeys": 1, "BusyTimeoutMilliseconds": 5000}.items():
            a.equal("SQLite " + key, at(database, key), expected)
        options = at(database, "CompileOptions")
        a.check("SQLite compile options recorded", isinstance(options, list) and len(options) > 20 and all(isinstance(x, str) for x in options))
        a.equal("loaded SQLite SHA-256", at(result, "LoadedSQLite.Sha256"), SQLITE_SHA)
        a.equal("plan SQLite SHA-256", at(plan, "inputs.sqliteLibrary.sha256"), SQLITE_SHA)
        a.input_file("sqliteLibrary", at(result, "LoadedSQLite.Path"), SQLITE_SHA)
        a.equal("selected mapped SQLite path", str(Path(flags["--native-library"]).resolve()), str(Path(at(result, "LoadedSQLite.Path")).resolve()))
    else:
        for key, expected in {"DefaultIsolation": "read committed", "DefaultReadOnly": "off", "QueryExecMode": "exec", "StatementCacheCapacity": 0,
                              "DescriptionCacheCapacity": 0, "NamedPreparedStatementsOnVerificationConnection": 0}.items():
            a.equal("PostgreSQL " + key, at(database, key), expected)
        a.check("PostgreSQL exact major/minor server", str(at(database, "DatabaseVersion")).startswith("PostgreSQL 18.0 "))
        for name, expected in POSTGRES_TOOLS.items():
            a.equal("plan PostgreSQL " + name + " SHA-256", at(plan, "inputs.postgresTools." + name + ".sha256"), expected)
            a.input_file("postgresTool:" + name, at(plan, "inputs.postgresTools." + name + ".path"), expected)
        a.equal("PostgreSQL affinity", at(plan, "postgres.cpus"), "4-7")
        a.equal("PostgreSQL maximum connections", at(plan, "postgres.maxConnections"), 64)
        a.check("no profiling in PostgreSQL plan", not any(k in at(plan, "postgres") for k in ("queryPlans", "activeCpuGroup")))
        settings_path = args.command.parent / "postgres-settings.txt"
        a.input_file("postgresSettings", settings_path)
        lines = settings_path.read_text().splitlines()
        a.check("PostgreSQL durability/locale/settings", len(lines) >= 11 and lines[0] == "db_perf_01" and lines[2:11] == ["on", "on", "on", "128MB", "read committed", "64", "UTF8", "UTF8", "C|C"], lines)
        owner_path = args.command.parent / "postgres-owner.json"
        a.input_file("postgresOwner", owner_path)
        owner = read_json(owner_path)
        a.equal("PostgreSQL owner exit", at(owner, "exitCode"), 0)
        a.check("PostgreSQL owner covers client command", integer(owner, "startedMonotonicNs") <= integer(command, "startedMonotonicNs") < integer(command, "finishedMonotonicNs") <= integer(owner, "finishedMonotonicNs"))
        a.derived["postgresLifetimeResources"] = {k: at(owner, k) for k in ("processLifetimeUserCpuSeconds", "processLifetimeSystemCpuSeconds", "processLifetimeMaxRssBytes")}
        a.derived["postgresLifetimeScope"] = "Owned server lifetime includes fixture and Go command; not a steady-operation server CPU denominator."
    return flags


def check_counts(a, result, manifest, seconds, rate, concurrency):
    o = at(result, "OfferedLoad")
    a.equal("offered rate", number(o, "RequestsPerSecond"), rate)
    a.equal("offered seconds", number(o, "OfferedSeconds"), seconds)
    a.equal("FIFO capacity", integer(o, "QueueCapacity"), 64)
    counts = {k: integer(o, k) for k in ("Planned", "Attempted", "Sent", "Started", "Completed", "Succeeded", "Failed", "Rejected", "Retries", "Rows", "SQLExecutions", "OracleFailures")}
    planned = math.ceil(seconds * rate)
    a.equal("planned = duration * rate", counts["Planned"], planned)
    a.equal("attempted = planned", counts["Attempted"], planned)
    a.equal("admitted + rejected = attempted", counts["Sent"] + counts["Rejected"], planned)
    a.equal("started = admitted", counts["Started"], counts["Sent"])
    a.equal("completed = success + failure", counts["Completed"], counts["Succeeded"] + counts["Failed"])
    a.equal("complete drain = started", counts["Completed"], counts["Started"])
    for key in ("Failed", "OracleFailures", "Retries"):
        a.equal("offered " + key + " =0", counts[key], 0)
    for key in ("Planned", "Started", "Failed", "Rejected", "Rows", "SQLExecutions", "OracleFailures", "Retries"):
        a.equal("top-level " + key, integer(result, key), counts[key])
    a.equal("top-level Completed means success", integer(result, "Completed"), counts["Succeeded"])
    a.check("no observed error strings/classes", empty(at(o, "Errors")) and empty(at(result, "Errors")) and empty(at(o, "ErrorClasses")))
    a.check("no cleanup error strings", empty(at(result, "CleanupErrors")))
    closed = at(result, "ApplicationConnectionsAfterClose")
    a.check("observed all application connections zero", isinstance(closed, list) and len(closed) == concurrency and all(type(x) is int and x == 0 for x in closed), closed, [0] * concurrency)
    a.equal("close flag consistent with observed counts", at(result, "ApplicationConnectionsReturnedToZero"), True)
    cases = at(result, "Cases")
    a.require("35 case count records", isinstance(cases, list) and len(cases) == 35)
    a.equal("nested cases match top-level", at(o, "Cases"), cases)
    case_totals = {key: 0 for key in ("Started", "Succeeded", "Failed", "Rows", "SQLExecutions")}
    rows_expected, sql_expected, digest_expected = 0, 0, 0
    for index, (case, query) in enumerate(zip(cases, manifest["queries"])):
        name = query["name"]
        a.equal("case name " + str(index), at(case, "Name"), name)
        values = {key: integer(case, key) for key in case_totals}
        a.equal(name + " started reconciles", values["Started"], values["Succeeded"] + values["Failed"])
        a.equal(name + " failed=0", values["Failed"], 0)
        a.check(name + " executed successfully", values["Succeeded"] > 0, values["Succeeded"])
        offered_for_case = (planned + 34 - index) // 35
        a.check(name + " global-arrival quota", values["Started"] <= offered_for_case, values["Started"], offered_for_case)
        expected_rows = values["Succeeded"] * query["expectedRows"]
        expected_sql = values["Succeeded"] * query["queryExecutionsPerOperation"]
        a.equal(name + " independent row count", values["Rows"], expected_rows)
        a.equal(name + " independent business SQL/control count", values["SQLExecutions"], expected_sql)
        rows_expected += expected_rows
        sql_expected += expected_sql
        if values["Succeeded"] % 2:
            digest_expected ^= int(query["expectedDigestHex"], 16)
        for key in case_totals:
            case_totals[key] += values[key]
    for key in case_totals:
        a.equal("case sums " + key, case_totals[key], counts[key])
    a.equal("independent total expected rows", counts["Rows"], rows_expected)
    a.equal("independent total expected SQL/control calls", counts["SQLExecutions"], sql_expected)
    a.equal("independent XOR of successful expected digests", at(o, "ResultDigestHex"), f"{digest_expected:016x}")
    a.derived["expectedRowsFromCaseCounts"] = rows_expected
    a.derived["expectedSQLFromCaseCounts"] = sql_expected
    a.derived["expectedDigestXorHex"] = f"{digest_expected:016x}"
    return o, counts


def check_time_histograms(a, result, command, o, counts, seconds, rate):
    frequency = integer(o, "TimerFrequency", 1)
    a.equal("timer frequency", frequency, 1_000_000_000)
    start, deadline, offered_end, drained = [integer(o, key) for key in ("StartedTicks", "DeadlineTicks", "OfferedEndTicks", "DrainedTicks")]
    a.equal("exact configured deadline", deadline - start, seconds * frequency)
    a.check("continuous offered clock through deadline and drained work", 0 < start < deadline <= offered_end <= drained, [start, deadline, offered_end, drained])
    a.equal("steady start identity", integer(result, "SteadyStartMonotonicNs"), start)
    a.equal("steady end identity", integer(result, "SteadyEndMonotonicNs"), drained)
    elapsed = (drained - start) / frequency
    a.check("effective duration recomputed", near(number(result, "EffectiveSeconds"), elapsed), at(result, "EffectiveSeconds"), elapsed)
    a.check("external command encloses steady boundaries", integer(command, "startedMonotonicNs") <= start < drained <= integer(command, "finishedMonotonicNs"))
    a.check("UTC operation window covers monotonic duration", (utc(at(result, "FinishedUtc")) - utc(at(result, "StartedUtc"))).total_seconds() + .01 >= elapsed)
    a.check("UTC command covers operation window", utc(at(command, "startedUtc")) <= utc(at(result, "StartedUtc")) < utc(at(result, "FinishedUtc")) <= utc(at(command, "finishedUtc")))
    expected_hist_counts = {"SuccessServiceHistogram": counts["Succeeded"], "SuccessScheduledHistogram": counts["Succeeded"], "AllCompletedScheduledHistogram": counts["Completed"], "StartDelayHistogram": counts["Started"]}
    histograms, percentiles = {}, {}
    for name, expected in expected_hist_counts.items():
        bins = histogram(o, name)
        histograms[name] = bins
        a.equal(name + " observations", sum(bins), expected)
        max_bin = max((i for i, n in enumerate(bins) if n), default=0)
        max_lower = (1 << (max_bin // 128)) * (1 + (max_bin % 128) / 128)
        a.check(name + " fits observed window", not expected or max_lower <= drained-start)
        percentiles[name] = quantiles(bins)
    if counts["Failed"] == 0:
        a.equal("all-completed equals successful response histogram", histograms["AllCompletedScheduledHistogram"], histograms["SuccessScheduledHistogram"])
    for field, source in (("SuccessServiceLatencyMicrosecondsUpper", "SuccessServiceHistogram"), ("SuccessScheduledLatencyMicrosecondsUpper", "SuccessScheduledHistogram")):
        for key, value in percentiles[source].items():
            a.check(field + " " + key + " recomputed", near(number(result, field + "." + key), value), at(result, field + "." + key), value)
    cumulative_service = cumulative_scheduled = cumulative_queue = 0
    dominance = True
    for service, scheduled, queued in zip(histograms["SuccessServiceHistogram"], histograms["SuccessScheduledHistogram"], histograms["StartDelayHistogram"]):
        cumulative_service += service
        cumulative_scheduled += scheduled
        cumulative_queue += queued
        if counts["Failed"] == 0 and (cumulative_scheduled > cumulative_service or cumulative_scheduled > cumulative_queue):
            dominance = False
    a.check("scheduled histogram cannot be faster than service or start-delay distribution", dominance)
    series = at(o, "Seconds")
    expected_length = min(math.ceil(seconds)+301, math.ceil(elapsed)+1)
    a.require("bounded complete second array", isinstance(series, list) and len(series) == expected_length, len(series) if isinstance(series, list) else None, expected_length)
    sums = {key: 0 for key in ("Planned", "Sent", "Started", "Succeeded", "Failed", "Rejected", "Rows")}
    bad_index, bad_plan, no_progress, causal_errors = [], [], [], []
    for index, sample in enumerate(series):
        if integer(sample, "Second") != index:
            bad_index.append(index)
        expected_plan = min(counts["Planned"], math.ceil((index+1)*rate)) - min(counts["Planned"], math.ceil(index*rate))
        if integer(sample, "Planned") != expected_plan:
            bad_plan.append(index)
        if index < seconds and integer(sample, "Succeeded") == 0:
            no_progress.append(index)
        for key in sums:
            sums[key] += integer(sample, key)
        if not sums["Succeeded"] + sums["Failed"] <= sums["Started"] <= sums["Sent"]:
            causal_errors.append(index)
    a.check("second indices contiguous from0", not bad_index, bad_index[:20])
    a.check("every offered second has exact planned arrival count", not bad_plan, bad_plan[:20])
    a.check("successful progress in every offered second", not no_progress, no_progress[:20])
    a.check("per-second cumulative admission/start/completion ordering", not causal_errors, causal_errors[:20])
    for key, total in sums.items():
        a.equal("second totals " + key, total, counts[key])
    a.equal("time-series flag consistent with complete evidence", at(o, "TimeSeriesTruncated"), False)
    a.derived["offeredElapsedSeconds"] = (offered_end-start)/frequency
    a.derived["postOfferedDrainSeconds"] = (drained-offered_end)/frequency
    a.derived["successfulProgressSeconds"] = seconds-len(no_progress)
    a.derived["histogramObservationCounts"] = {key: sum(value) for key, value in histograms.items()}
    a.metrics.update({"effective_seconds": elapsed, "offered_seconds": seconds,
                      "successful_operations_per_second": counts["Succeeded"]/elapsed,
                      "service_us_upper": percentiles["SuccessServiceHistogram"],
                      "scheduled_us_upper": percentiles["SuccessScheduledHistogram"],
                      "start_delay_us_upper": percentiles["StartDelayHistogram"]})
    a.check("reported throughput recomputed", near(number(result, "SuccessPerSecond"), counts["Succeeded"]/elapsed))


def check_resources(a, result, command, o, counts, args, idle):
    a.equal("external exit code", at(command, "exitCode"), 0)
    a.equal("external timeout flag", at(command, "timedOut"), False)
    a.check("external observer errors empty", empty(at(command, "observerErrors")))
    command_seconds = (integer(command, "finishedMonotonicNs") - integer(command, "startedMonotonicNs"))/1e9
    a.check("external finite command deadline", 0 < command_seconds < number(command, "timeoutSeconds") + 1, command_seconds)
    resource_name = at(command, "resourceSamples")
    a.require("resource evidence file has local basename", isinstance(resource_name, str) and Path(resource_name).name == resource_name)
    file_hashes = at(command, "files")
    a.require("external evidence hashes include resources and stdout", isinstance(file_hashes, dict) and resource_name in file_hashes and at(command, "name")+".stdout.log" in file_hashes)
    for name, expected in file_hashes.items():
        a.require("external evidence local path " + name, Path(name).name == name)
        a.input_file("external:"+name, args.command.parent/name, expected)
    # Hash verification is sufficient for the runner's raw JSONL identity; its
    # non-atomic sampled process trees are not relabeled as exact server CPU.
    before, after = at(result, "MemoryBefore"), at(result, "MemoryAfter")
    delta = integer(after, "TotalAlloc") - integer(before, "TotalAlloc")
    a.check("process TotalAlloc monotonic", delta >= 0)
    for key, expected in {"AllocatedBefore": integer(before, "TotalAlloc"), "AllocatedAfter": integer(after, "TotalAlloc"), "AllocatedBytes": delta}.items():
        a.equal("allocation " + key, integer(result, key), expected)
    for result_key, counter in (("GoGCCount", "NumGC"), ("GoForcedGCCount", "NumForcedGC"), ("GoGCPauseNs", "PauseTotalNs")):
        measured = integer(after, counter) - integer(before, counter)
        a.check(counter + " monotonic", measured >= 0)
        a.equal(result_key + " recomputed", integer(result, result_key), measured)
    a.equal("no forced GC during measured work", integer(result, "GoForcedGCCount"), 0)
    offered_memory = at(o, "MemoryAtOfferedEnd")
    for key in ("TotalAlloc", "NumGC", "NumForcedGC", "PauseTotalNs"):
        a.check("offered-end " + key + " inside process snapshots", integer(before, key) <= integer(offered_memory, key) <= integer(after, key))
    b_cpu, e_cpu = at(result, "ProcessBefore"), at(result, "ProcessAfter")
    user = number(e_cpu, "UserCpuSeconds") - number(b_cpu, "UserCpuSeconds")
    system = number(e_cpu, "SystemCpuSeconds") - number(b_cpu, "SystemCpuSeconds")
    a.check("client CPU counters monotonic", user >= 0 and system >= 0)
    a.check("user CPU delta recomputed", near(number(result, "UserCpuSeconds"), user))
    a.check("system CPU delta recomputed", near(number(result, "SystemCpuSeconds"), system))
    a.check("lifetime user CPU encloses measured CPU", number(command, "processLifetimeUserCpuSeconds") + .001 >= user)
    a.check("lifetime system CPU encloses measured CPU", number(command, "processLifetimeSystemCpuSeconds") + .001 >= system)
    maximum_rss = integer(command, "processLifetimeMaxRssBytes", 1)
    # /proc VmRSS can be slightly larger than getrusage ru_maxrss at a nearby
    # observation. Do not invent a strict cross-interface equality requirement.
    for label, process in (("before", b_cpu), ("after", e_cpu), ("exit", at(result, "ProcessAtExit"))):
        a.check("nonzero process RSS " + label, integer(process, "RssBytes") > 0)
        a.check("process recorded maxRSS fits wait4 " + label, integer(process, "PeakRssBytes") <= maximum_rss)
    cgo = at(result, "CgoCalls")
    a.equal("process cgo-call delta", integer(cgo, "Delta"), integer(cgo, "After")-integer(cgo, "Before"))
    if at(result, "Provider") == "sqlite":
        a.check("SQLite observed cgo calls", integer(cgo, "Delta") > 0)
    successful = counts["Succeeded"]
    a.require("nonzero successful denominator", successful > 0)
    bytes_per = delta/successful
    cpu_per = (user+system)/successful
    a.check("allocation per success recomputed", near(number(result, "BytesPerSuccessfulOperation"), bytes_per))
    a.check("CPU per success recomputed", near(number(result, "CpuSecondsPerSuccessfulOperation"), cpu_per))
    a.metrics.update({"planned": counts["Planned"], "admitted": counts["Sent"], "successful": successful,
                      "failed": counts["Failed"], "oracle_failures": counts["OracleFailures"], "rejected": counts["Rejected"],
                      "rejection_fraction": counts["Rejected"]/counts["Attempted"], "business_rows": counts["Rows"], "business_sql_control_calls": counts["SQLExecutions"],
                      "client_user_cpu_seconds": user, "client_system_cpu_seconds": system, "client_cpu_microseconds_per_success": cpu_per*1e6,
                      "client_cpu_core_equivalents": (user+system)/number(result, "EffectiveSeconds"),
                      "go_heap_allocated_bytes": delta, "go_heap_bytes_per_success": bytes_per, "go_gc_cycles": integer(result, "GoGCCount"),
                      "go_gc_pause_milliseconds": integer(result, "GoGCPauseNs")/1e6,
                      "cgo_calls": integer(cgo, "Delta"), "whole_command_peak_rss_bytes": maximum_rss,
                      "post_drain_idle_requested_seconds": idle, "post_drain_idle_rss_bytes": None, "post_drain_idle_pss_bytes": None})
    if idle:
        reading = at(result, "Idle")
        a.equal("idle requested seconds", number(reading, "RequestedSeconds"), idle)
        observed = number(reading, "ObservedSeconds")
        a.check("observed idle reaches requested period", observed + .005 >= idle, observed, idle)
        utc_idle = (utc(at(reading, "FinishedUtc")) - utc(at(reading, "StartedUtc"))).total_seconds()
        a.check("idle UTC/monotonic consistency", abs(utc_idle-observed) <= .01, utc_idle, observed)
        a.check("idle occurs after operation drain", utc(at(reading, "StartedUtc")) >= utc(at(result, "FinishedUtc")))
        a.check("external command includes idle", utc(at(reading, "FinishedUtc")) <= utc(at(command, "finishedUtc")))
        a.equal("no forced GC during idle", integer(reading, "MemoryAfter.NumForcedGC"), integer(reading, "MemoryBefore.NumForcedGC"))
        after_idle = at(reading, "After")
        a.metrics["post_drain_idle_rss_bytes"] = integer(after_idle, "RssBytes", 1)
        pss = at(after_idle, "PssBytes")
        if pss is not None and (type(pss) is not int or pss <= 0):
            raise InvalidEvidence("idle PSS must be positive or explicitly unavailable")
        a.metrics["post_drain_idle_pss_bytes"] = pss
        a.metrics["post_drain_idle_observed_seconds"] = observed
        idle_user = number(after_idle, "UserCpuSeconds") - number(reading, "Before.UserCpuSeconds")
        idle_system = number(after_idle, "SystemCpuSeconds") - number(reading, "Before.SystemCpuSeconds")
        a.check("idle CPU counters monotonic", idle_user >= 0 and idle_system >= 0)
        a.metrics["post_drain_idle_cpu_seconds"] = idle_user+idle_system
        a.metrics["post_drain_idle_go_heap_bytes"] = integer(reading, "MemoryAfter.HeapAlloc")
    else:
        a.check("preflight has no falsely labeled idle period", "Idle" not in result)
    return command_seconds


def perform(args, audit):
    for name, path in (("result", args.result), ("command", args.command), ("manifest", args.manifest), ("plan", args.plan), ("verifier", Path(__file__))):
        audit.input_file(name, path)
    result, command, manifest, plan = [read_json(path) for path in (args.result, args.command, args.manifest, args.plan)]
    for name, value in (("result", result), ("command", command), ("manifest", manifest), ("plan", plan)):
        audit.require(name + " is object", isinstance(value, dict))
    provider = at(result, "Provider")
    audit.require("supported provider", provider in ("sqlite", "postgresql"), provider)
    concurrency, rate = (4, 3000) if provider == "sqlite" else (8, 3600)
    seconds, idle = (5, 0) if args.kind == "preflight" else (1800, 15)
    audit.metrics["provider"] = provider
    audit.metrics["kind"] = args.kind
    check_identity(audit, result, command, plan, manifest, args, provider, seconds, rate, concurrency, idle)
    offered, counts = check_counts(audit, result, manifest, seconds, rate, concurrency)
    check_time_histograms(audit, result, command, offered, counts, seconds, rate)
    check_resources(audit, result, command, offered, counts, args, idle)
    audit.derived["ignoredAsAcceptanceAuthorities"] = ["TimingComparisonEligible", "WarmupVerified", "command.success", "plan.longRunAcceptance", "command.longRunAcceptance"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--result", type=Path, required=True)
    parser.add_argument("--command", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--plan", type=Path)
    parser.add_argument("--kind", choices=("preflight", "final"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.plan = args.plan or args.command.parent/"plan.json"
    if args.output.exists():
        parser.error("acceptance output already exists; preserve it and choose a fresh path")
    audit = Audit()
    try:
        perform(args, audit)
    except (InvalidEvidence, ValueError, KeyError, TypeError, OSError, IndexError, OverflowError) as error:
        audit.check("complete evidence can be independently decoded", False, f"{type(error).__name__}: {error}")
    passed = bool(audit.checks) and all(check["passed"] for check in audit.checks)
    receipt = {"schemaVersion": 1, "kind": args.kind, "accepted": passed,
               "finalThirtyMinuteAccepted": passed and args.kind == "final", "preflightAccepted": passed and args.kind == "preflight",
               "auditedUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
               "method": "Recomputed from immutable manifest, per-case/per-second counts, histograms, cumulative runtime/process observations and external runner evidence; no database calls or load execution.",
               "scope": "Go comparator only. A passing5s preflight is not30-minute evidence. Metrics are client process unless explicitly labeled otherwise; Go heap/GC are not .NET generation-equivalent.",
               "inputs": audit.inputs, "checks": audit.checks, "failedChecks": [check for check in audit.checks if not check["passed"]],
               "derived": audit.derived, "metrics": audit.metrics}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(receipt, output, indent=2, ensure_ascii=False, allow_nan=False)
        output.write("\n")
    print(json.dumps({"accepted": passed, "finalThirtyMinuteAccepted": receipt["finalThirtyMinuteAccepted"], "checks": len(audit.checks),
                      "failedChecks": receipt["failedChecks"], "metrics": audit.metrics, "output": str(args.output.resolve())}, ensure_ascii=False, allow_nan=False))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
