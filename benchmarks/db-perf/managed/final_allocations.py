#!/usr/bin/env python3
"""Bind the unchanged external allocation consumer to exact final CI packages.

The original allocations/summarize.py intentionally asserts the fixed-main
commit. This separate validator accepts an explicitly supplied final commit;
it does not edit that baseline validator or any measured C# consumer source.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def write(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n")


def candidate_inputs(directory, commit):
    result = {}
    require(len(commit) == 40 and all(c in "0123456789abcdef" for c in commit), "Expected a complete lowercase source commit")
    for provider, suffix in (("sqlite", "Sqlite"), ("postgresql", "PostgreSql")):
        candidate = directory / provider
        release_path = candidate / "release.json"
        release = json.loads(release_path.read_text())
        require(release["source_commit"] == commit and release["source_dirty"] is False,
                f"{provider}: final CI source identity or cleanliness differs")
        require(release["rid"] == "linux-x64", f"{provider}: wrong RID")
        packages = list((candidate / "feed").glob("*.nupkg"))
        require(len(packages) == 1, f"{provider}: expected exactly one top-level package")
        package = packages[0]
        package_id = "Korda.Database." + suffix
        entries = [item for item in release["packages"] if item["id"] == package_id]
        require(len(entries) == 1, f"{provider}: missing or duplicate package receipt")
        entry = entries[0]
        require(entry["source_commit"] == commit and entry["native"]["source_commit"] == commit,
                f"{provider}: package/native provenance differs")
        require(sha(package) == entry["upload_archive_sha256"], f"{provider}: archive differs from exact CI upload")
        with zipfile.ZipFile(package) as archive:
            unsigned = {name for name in archive.namelist() if not name.endswith("/") and name != ".signature.p7s"}
            require(unsigned == set(entry["unsigned_entries"]), f"{provider}: package entry inventory differs")
            for name, digest in entry["unsigned_entries"].items():
                require(hashlib.sha256(archive.read(name)).hexdigest() == digest, f"{provider}: entry hash differs: {name}")
            nuspecs = [name for name in unsigned if name.endswith(".nuspec")]
            require(len(nuspecs) == 1, f"{provider}: ambiguous package metadata")
            metadata = ET.fromstring(archive.read(nuspecs[0])).find("{*}metadata")
            require(metadata is not None, f"{provider}: missing metadata")
            require(metadata.findtext("{*}id") == package_id and metadata.findtext("{*}version") == entry["version"],
                    f"{provider}: nuspec ID/version differs")
            repository = metadata.find("{*}repository")
            require(repository is not None and repository.get("commit") == commit, f"{provider}: nuspec source differs")
            require(hashlib.sha256(archive.read(entry["native"]["path"])).hexdigest() == entry["native"]["sha256"],
                    f"{provider}: native bytes differ")
        result[provider] = {"package_id": package_id, "version": entry["version"], "source_commit": commit,
                            "package_path": str(package), "package_sha256": sha(package),
                            "release_receipt_sha256": sha(release_path), "native_sha256": entry["native"]["sha256"]}
    return result


def validate(evidence, *, expected=None):
    manifest = json.loads((evidence / "build-manifest.json").read_text())
    require(manifest["postgres_cluster"]["exit_code"] == 0, "Dedicated PostgreSQL did not stop cleanly")
    processes = set()
    runs = []
    for entry in manifest["runs"]:
        raw_path = evidence / entry["result"]
        require(sha(raw_path) == entry["result_sha256"], "Raw result hash differs")
        raw = json.loads(raw_path.read_text())
        provider = entry["provider"]
        identity = manifest["providers"][provider]
        if expected is not None:
            for field in ("package_id", "version", "source_commit", "package_sha256", "release_receipt_sha256", "native_sha256"):
                require(identity[field] == expected[provider][field], f"{provider}: manifest {field} differs from final package")
        require(raw["native_aot"] and raw["warmup_per_scenario"] >= 50, "AOT or warmup proof is absent")
        require(raw["source_commit"] == identity["source_commit"], "Consumer source commit differs")
        require(raw["process_id"] not in processes, "A process identity was repeated")
        processes.add(raw["process_id"])
        for field in ("package_sha256", "executable_sha256"):
            require(raw[field] == identity[field], f"Consumer {field} differs")
        require(raw["loaded_native_sha256"] == identity["native_sha256"], "Actually loaded native library differs")
        require(sha(identity["executable_path"]) == identity["executable_sha256"], "Measured executable bytes changed")
        require(raw["native_counters_after_source_close"][:6] == [0] * 6, "A live native owner remains")
        intervals = raw["complete_queries"] + raw["within_first_batch"] + raw["supplemental_fetch_including_first_row"] + raw["empty_sync_control"] + [raw["empty_async_control"], raw["source_open_idle_control"]]
        for value in intervals:
            delta = value["end"]["allocated_bytes"] - value["start"]["allocated_bytes"]
            require(value["allocated_bytes_total"] == delta and delta >= 0, "Raw all-thread byte arithmetic differs")
            require(value["framework_allocated_bytes"] is None, "This probe cannot claim an exact framework split")
            for generation in range(3):
                delta = value["end"][f"gc{generation}"] - value["start"][f"gc{generation}"]
                require(value[f"gc{generation}_delta"] == delta and delta >= 0, "Raw GC arithmetic differs")
        complete = []
        for value in raw["complete_queries"]:
            require(value["operations"] == raw["measured_iterations"] and value["operations"] > 0, "Query operation count differs")
            require(value["rows"] == value["operations"] * 256 and value["checksum"] == value["operations"] * 32896,
                    "Complete result consumption or digest differs")
            complete.append({key: value[key] for key in ("name", "operations", "rows", "checksum", "allocated_bytes_total", "incomplete_move_observations", "await_thread_changes", "gc0_delta", "gc1_delta", "gc2_delta")})
        windows = {}
        for name in ("within_first_batch", "supplemental_fetch_including_first_row", "empty_sync_control"):
            values = raw[name]
            require(len(values) == raw["measured_iterations"], "A narrow window is missing")
            for value in values:
                if name == "supplemental_fetch_including_first_row":
                    require(value["operations"] == value["rows"] == 1 and value["checksum"] == 129, "Supplemental first-row boundary differs")
                else:
                    require(value["operations"] == value["rows"] == 127 and value["checksum"] == 8255, "Within-batch boundary differs")
                    require(value["incomplete_move_observations"] == value["await_thread_changes"] == 0, "A synchronous window actually waited")
                    require(value["start"]["managed_thread_id"] == value["end"]["managed_thread_id"], "A synchronous window changed thread")
            counts = [value["allocated_bytes_total"] for value in values]
            windows[name] = {"windows": len(values), "raw_allocated_bytes": counts, "raw_total": sum(counts),
                             "rows": sum(value["rows"] for value in values), "all_raw_zero": all(value == 0 for value in counts),
                             "incomplete_move_observations": sum(value["incomplete_move_observations"] for value in values),
                             "actual_await_thread_changes": sum(value["await_thread_changes"] for value in values)}
        runs.append({"provider": provider, "repetition": entry["repetition"], "process_id": raw["process_id"],
                     "raw_result": str(raw_path), "raw_sha256": sha(raw_path), "complete_queries": complete, "windows": windows,
                     "empty_async_bytes": raw["empty_async_control"]["allocated_bytes_total"],
                     "source_open_idle_bytes": raw["source_open_idle_control"]["allocated_bytes_total"],
                     "native_counters_after_source_close": raw["native_counters_after_source_close"]})
    counts = Counter(run["provider"] for run in runs)
    require(counts["sqlite"] >= 3 and counts["postgresql"] >= 3, "Need three independent processes for each provider")
    return {"manifest_sha256": sha(evidence / "build-manifest.json"), "source_files": manifest["source_files"],
            "providers": manifest["providers"], "runs": runs}


def totals(validation):
    result = {}
    for run in validation["runs"]:
        for value in run["complete_queries"]:
            key = run["provider"] + "/" + value["name"]
            total = result.setdefault(key, {"operations": 0, "rows": 0, "raw_allocated_bytes": 0, "per_process_raw_bytes": []})
            total["operations"] += value["operations"]
            total["rows"] += value["rows"]
            total["raw_allocated_bytes"] += value["allocated_bytes_total"]
            total["per_process_raw_bytes"].append(value["allocated_bytes_total"])
    for value in result.values():
        value["bytes_per_operation"] = str(Decimal(value["raw_allocated_bytes"]) / value["operations"])
        value["bytes_per_row"] = str(Decimal(value["raw_allocated_bytes"]) / value["rows"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--expected-source-commit", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--report-dir", type=Path, required=True, help="Fresh output; separate from immutable older evidence when validating it")
    parser.add_argument("--baseline-evidence", type=Path)
    parser.add_argument("--label", default="final-ci-candidate")
    parser.add_argument("--validate-only", action="store_true")
    for name in ("work", "dotnet", "clang-bin", "postgres-bin", "support-cache"):
        parser.add_argument("--" + name, type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[3]
    consumer = repo / "benchmarks/Database/allocations"
    report_dir = args.report_dir.resolve()
    require(not report_dir.exists() or not any(report_dir.iterdir()), "Do not overwrite prior validation evidence")
    report_dir.mkdir(parents=True, exist_ok=True)
    receipt = {"schemaVersion": 1, "label": args.label, "startedUtc": utc(), "driverSha256": sha(__file__),
               "status": "started", "validationOnly": args.validate_only, "fullFrameworkZeroAllocation": False,
               "frameworkSplit": "unproved; no estimated control subtraction", "subtractedBytes": 0,
               "scope": "Exact-CI-package external Native AOT allocation preflight, not NuGet publication verification or a dedicated long performance segment"}
    try:
        inputs = candidate_inputs(args.artifacts.resolve(), args.expected_source_commit)
        receipt["candidateInputs"] = inputs
        source_files = {name: sha(consumer / name) for name in ("AllocationProbe.csproj", "Program.cs", "Directory.Build.props", "global.json")}
        receipt["consumerSourceFiles"] = source_files
        receipt["consumerRunnerSha256"] = sha(consumer / "run.py")
        if not args.validate_only:
            for name in ("work", "dotnet", "clang_bin", "postgres_bin", "support_cache"):
                require(getattr(args, name) is not None, "Missing --" + name.replace("_", "-"))
            for path in (args.work, args.evidence):
                require(not path.exists() or not any(path.iterdir()), "Use fresh external work and evidence directories")
            command = [sys.executable, str(consumer / "run.py"), "--artifacts", str(args.artifacts.resolve()),
                       "--work", str(args.work.resolve()), "--evidence", str(args.evidence.resolve()),
                       "--dotnet", str(args.dotnet.resolve()), "--clang-bin", str(args.clang_bin.resolve()),
                       "--postgres-bin", str(args.postgres_bin.resolve()), "--support-cache", str(args.support_cache.resolve()),
                       "--warmup", "64", "--iterations", "128", "--processes", "3"]
            receipt["command"] = command
            write(report_dir / "execution.json", receipt)
            with (report_dir / "run.log").open("x") as log:
                code = subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT).returncode
            receipt["exitCode"] = code
            require(code == 0, "Candidate run failed; all partial logs and counts are retained")
        after = validate(args.evidence.resolve(), expected=inputs)
        require(after["source_files"] == source_files, "External consumer differs from the unchanged benchmark source")
        receipt["candidateValidation"] = after
        receipt["completeQueryTotals"] = totals(after)
        if args.baseline_evidence:
            baseline = validate(args.baseline_evidence.resolve())
            require(baseline["source_files"] == after["source_files"], "Before/after consumer inputs differ")
            receipt["baselineManifestSha256"] = baseline["manifest_sha256"]
            receipt["baselineCompleteQueryTotals"] = totals(baseline)
            receipt["bytesPerOperationChange"] = {}
            for key, new in receipt["completeQueryTotals"].items():
                old = receipt["baselineCompleteQueryTotals"][key]
                delta = Decimal(new["bytes_per_operation"]) - Decimal(old["bytes_per_operation"])
                receipt["bytesPerOperationChange"][key] = {"absolute": str(delta),
                    "percent": str(delta / Decimal(old["bytes_per_operation"]) * 100) if Decimal(old["bytes_per_operation"]) else None}
        receipt["status"] = "verified"
    except BaseException as error:
        receipt.update(status="failed", error={"type": type(error).__name__, "message": str(error)})
        raise
    finally:
        receipt["finishedUtc"] = utc()
        write(report_dir / "execution.json", receipt)
    print(json.dumps({"status": receipt["status"], "label": args.label,
                      "independentProcesses": len(receipt["candidateValidation"]["runs"]),
                      "report": str(report_dir / "execution.json")}))


if __name__ == "__main__":
    main()
