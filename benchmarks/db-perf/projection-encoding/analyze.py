#!/usr/bin/env python3
"""Verify three raw projection-encoder baseline processes without executing them."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import struct


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(value, message):
    if not value:
        raise ValueError(message)


def record(kind, fields):
    """Independent fixed B01 wire construction, using only explicit fixture data."""
    start = 16 + 16 * len(fields)
    payload = b"".join(value for _, _, value in fields)
    header = struct.pack("<HHIII", 1, kind, start + len(payload), len(fields), 0)
    descriptors = bytearray()
    for field_id, field_type, value in fields:
        descriptors += struct.pack("<HHIQ", field_id, field_type, len(value), start)
        start += len(value)
    return header + descriptors + payload


def expected_field(index):
    return {"ordinal": index, "name": "field_" + str(index) if index % 2 == 0 else "订单_" + str(index) + "_🦀",
            "codec": [3, 5, 13, 4][index % 4],
            "codec_config": {"precision": 18, "scale": 2} if index % 4 == 2 else None,
            "nullable": index % 4 == 1}


def expected_wire(count, expressions, shape_hash):
    nested = bytearray(struct.pack("<I", count))
    for index in range(count):
        field = expected_field(index)
        values = [(1, 1, struct.pack("<I", index)), (2, 4, field["name"].encode()),
                  (3, 1, struct.pack("<I", field["codec"])), (4, 1, struct.pack("<I", int(field["nullable"])))]
        if expressions:
            values.append((5, 2, struct.pack("<Q", 0x100000001 + index)))
        encoded = record(22, values)
        nested += struct.pack("<I", len(encoded)) + encoded
    return record(13, [(1, 5, bytes(nested)), (2, 5, bytes.fromhex(shape_hash))])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.directory.resolve()
    build_path = root / "before-publish-v2.json"
    build = json.loads(build_path.read_text())
    require(build["exitCode"] == 0 and build["inputsUnchanged"] and not build["productionSourceModifiedFromBase"], "Baseline build identity failed")
    require("-p:PublishAot=true" in build["argv"] and "-p:RestoreLockedMode=true" in build["argv"], "Expected locked Native AOT publish")
    first_failure = json.loads((root / "before-publish.json").read_text())
    require(first_failure["exitCode"] == 1 and "CS0453" in (root / "before-publish.stdout.log").read_text(), "Original compiler failure was not retained")
    expected_names = [f"fields-{count}-{mode}" for count in (1, 4, 8, 32, 128) for mode in ("metadata", "expressions")]
    raw_results, commands, pids, hashes = [], [], set(), {}
    exceptions = None
    for repeat in range(1, 4):
        command_path, result_path = root / f"before-{repeat:02d}.command.json", root / f"before-{repeat:02d}.result.json"
        command, raw = json.loads(command_path.read_text()), json.loads(result_path.read_text())
        require(command["exitCode"] == 0 and command["executableUnchanged"] and command["sourceUnchanged"], "Original process did not complete unchanged")
        require(command["buildReceiptSha256"] == sha(build_path), "Process used a different build receipt")
        for name, identity in command["files"].items():
            path = root / name
            require(path.parent == root and path.stat().st_size == identity["bytes"] and sha(path) == identity["sha256"], "Bound raw bytes changed")
        require(raw["nativeAot"] is True and raw["architecture"] == "X64" and raw["processId"] == command["pid"], "Wrong runtime or child identity")
        require(raw["processId"] not in pids and raw["sourceBaseCommit"] == build["sourceBaseCommit"], "Repeated process identity or wrong source base")
        pids.add(raw["processId"])
        require(raw["sourceSha256"] == command["sourceSha256"] == build["sourceFiles"]["src/Query.Binding/OperandEncoding.cs"]
                and raw["executableSha256"] == command["executableSha256"] == sha(Path(command["argv"][0])), "ELF/source binding differs")
        require(raw["operationsPerCase"] == 4096 and raw["warmupPerCase"] == 1024 and raw["businessResultAllocatedBytes"] == 0,
                "Frozen measurement boundary changed")
        require([case["name"] for case in raw["cases"]] == expected_names, "Missing or reordered baseline case")
        previous_after = previous_ticks = 0
        for case_index, case in enumerate(raw["cases"]):
            count, expressions = case["fieldCount"], case["expressionCount"]
            require(count == (1, 4, 8, 32, 128)[case_index // 2]
                    and expressions == (count if case_index % 2 else 0), "Metadata/expression field-count fixture changed")
            require(expressions in (0, count) and case["inputUnchanged"]
                    and case["fixtureSha256Before"] == case["fixtureSha256After"], "Projection input mutation or expression shape differs")
            canonical = bytes.fromhex(case["canonicalMetadataHex"])
            require(hashlib.sha256(canonical).hexdigest() == case["shapeHash"]
                    and json.loads(canonical) == {"version": 1, "fields": [expected_field(i) for i in range(count)]},
                    "Canonical metadata or logical shape hash differs")
            expected = expected_wire(count, expressions, case["shapeHash"])
            require(bytes.fromhex(case["encodedHex"]) == expected and case["encodedBytes"] == len(expected), "Complete independently constructed wire golden differs")
            require(case["bindingOrder"] == [[3, 5, 13, 4][i % 4] for i in range(count)], "Binder order or count differs")
            require(case["checksum"] == sum(expected) * 4096, "Complete-byte operation count does not reconcile")
            require(type(case["allocatedBefore"]) is int and type(case["allocatedAfter"]) is int
                    and case["allocatedBefore"] >= previous_after and case["allocatedAfter"] >= case["allocatedBefore"]
                    and case["allocatedBytes"] == case["allocatedAfter"] - case["allocatedBefore"]
                    and case["allocatedBytesPerOperation"] == case["allocatedBytes"] / 4096, "Whole-process raw allocation endpoints differ")
            require(case["endedTicks"] > case["startedTicks"] >= previous_ticks, "Operation time windows overlap or regress")
            for generation in range(3):
                require(type(case[f"gc{generation}Before"]) is int and type(case[f"gc{generation}After"]) is int
                        and case[f"gc{generation}After"] >= case[f"gc{generation}Before"] >= 0, "GC counters regress")
            for kind in ("user", "system"):
                require(case[kind + "CpuSecondsAfter"] >= case[kind + "CpuSecondsBefore"] >= 0, "CPU counters regress")
            previous_after, previous_ticks = case["allocatedAfter"], case["endedTicks"]
        require([e["name"] for e in raw["exceptions"]] == ["null-projection", "null-binder", "expression-length", "binding-failure-third-field"], "Missing exception observation")
        require([e["type"] for e in raw["exceptions"]] == ["System.NullReferenceException", "System.NullReferenceException", "System.ArgumentException", "System.ApplicationException"], "Baseline exception types changed")
        require(raw["exceptions"][-1]["bindingOrder"] == [3, 5, 13], "Failing binder visitation order changed")
        require(exceptions is None or exceptions == raw["exceptions"], "Exception type/message/binder sequence differs between processes")
        exceptions = raw["exceptions"]
        hashes[result_path.name], hashes[command_path.name] = sha(result_path), sha(command_path)
        raw_results.append(raw)
        commands.append(command)
    rows = []
    for index, name in enumerate(expected_names):
        values = [raw["cases"][index] for raw in raw_results]
        require(len({item["encodedHex"] for item in values}) == 1, "Wire golden changed between processes")
        rows.append({"name": name, "fieldCount": values[0]["fieldCount"], "expressionCount": values[0]["expressionCount"],
            "encodedBytes": values[0]["encodedBytes"], "wireSha256": hashlib.sha256(bytes.fromhex(values[0]["encodedHex"])).hexdigest(),
            "operationsPerProcess": 4096, "totalMeasuredOperations": 12288,
            "rawAllocatedBytes": [v["allocatedBytes"] for v in values],
            "allocatedBytesPerOperation": [v["allocatedBytesPerOperation"] for v in values],
            "medianAllocatedBytesPerOperation": statistics.median(v["allocatedBytesPerOperation"] for v in values),
            "gcDeltas": [[v[f"gc{g}After"] - v[f"gc{g}Before"] for g in range(3)] for v in values],
            "durationSeconds": [(v["endedTicks"] - v["startedTicks"]) / raw_results[i]["stopwatchFrequency"] for i, v in enumerate(values)],
            "userCpuSeconds": [v["userCpuSecondsAfter"] - v["userCpuSecondsBefore"] for v in values],
            "systemCpuSeconds": [v["systemCpuSecondsAfter"] - v["systemCpuSecondsBefore"] for v in values]})
    report = {"schemaVersion": 1, "task": "DB-Perf-01", "kind": "projection-encoding-before-only-baseline",
        "sourceBaseCommit": build["sourceBaseCommit"], "sourceSha256": raw_results[0]["sourceSha256"],
        "executableSha256": raw_results[0]["executableSha256"], "nativeAot": True, "independentProcesses": 3,
        "caseCount": 10, "totalMeasuredOperations": 122880, "rawResults": hashes,
        "buildReceiptSha256": sha(build_path), "analyzerSha256": sha(Path(__file__)), "cases": rows, "exceptions": exceptions,
        "goldensVerifiedIndependently": True, "inputMutationChecksPassed": True, "productionSourceModified": False,
        "allocationScope": "Prebuilt fixtures and binder; whole-process measured bytes include framework output arrays and temporary encoding state. No business object result is created in this isolated fixture. No subtraction assigns GC or unexplained runtime increments to framework/business categories.",
        "limits": ["No new optimized production path or after measurement exists.", "The first small-field window may include process initialization triggered by its first GC; preserve its literal count without calling the residual framework-only overhead.", "CPU/duration on this shared host are raw observations, not stable performance conclusions.", "The binder returns logical IDs and does not perform native configured-codec registration.", "Not public PackageReference, database, SQL, service latency or long-run acceptance."],
        "acceptedLongSegments": 0, "wholeBatchAcceptance": False}
    with args.output.open("x") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
    print(json.dumps({"passed": True, "independentProcesses": 3, "cases": 10, "measuredOperations": 122880,
                      "output": str(args.output), "casesBytesPerOperation": {row["name"]: row["allocatedBytesPerOperation"] for row in rows}}))


if __name__ == "__main__":
    main()
