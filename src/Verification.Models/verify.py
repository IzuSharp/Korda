#!/usr/bin/env python3
"""Run A19's bounded models, preserve counterexamples and record exact identity."""

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import platform
import subprocess
import sys

from checker import TOOL_VERSION, explore, replay
from identity import digest, source_manifest, verify_design_inputs, verify_refinement
from suite import models, oracle_met


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", default="artifacts/A19/linux", help="New evidence directory; existing result files are not overwritten")
    parser.add_argument("--design-root", type=Path, help="Exact Plan-locked Design checkout; verifies all task input hashes")
    parser.add_argument("--model", action="append", choices=[model.name for model in models()], help="Select cases; omitted means the complete suite")
    parser.add_argument("--max-states", type=int, default=200000)
    parser.add_argument("--timeout-seconds", type=float, default=30.0, help="Per-model exploration and progress limit")
    parser.add_argument("--refinement", type=Path, help="Optional later implementation-to-transition binding manifest")
    parser.add_argument("--implementation-root", type=Path, help="Exact implementation checkout for --refinement")
    args = parser.parse_args(argv)
    if args.max_states < 1 or args.timeout_seconds <= 0:
        parser.error("State and time limits must be positive")
    if bool(args.refinement) != bool(args.implementation_root):
        parser.error("--refinement and --implementation-root must be supplied together")
    root = Path(__file__).resolve().parents[2]
    output = Path(args.output_dir).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        parser.error("Evidence directory must be empty; choose a new attempt path")
    started = utc_now()
    pin_path = Path(__file__).with_name("design-inputs.json")
    pin = json.loads(pin_path.read_text())
    design_validation = {"status": "not_revalidated_locally", "commit": pin["design_commit"],
                         "reason": "Supply --design-root for acceptance input validation."}
    try:
        if args.design_root:
            design_validation = verify_design_inputs(pin, args.design_root)
        refinement = {"status": "not_yet_bound", "reason": "A03/A06/A07/A08 product algorithms are owned by future tasks."}
        if args.refinement:
            mapping = json.loads(Path(__file__).with_name("transition-map.json").read_text())
            refinement = verify_refinement(json.loads(args.refinement.read_text()), args.implementation_root,
                                           [row["action"] for row in mapping["refinement_boundaries"]])
    except (OSError, ValueError, subprocess.CalledProcessError, KeyError) as error:
        failure = {"status": "failed_input_validation", "started_utc": started, "ended_utc": utc_now(),
                   "error": str(error), "exit_code": 1, "argv": sys.argv}
        (output / "input-failure.json").write_text(json.dumps(failure, indent=2) + "\n")
        print(json.dumps(failure), file=sys.stderr)
        return 1
    selected = [model for model in models() if not args.model or model.name in args.model]
    cases = []
    for model in selected:
        result = explore(model, args.max_states, args.timeout_seconds)
        result["expected_violation"] = model.expected_violation or None
        result["oracle_met"] = oracle_met(model, result)
        for name, trace in result["counterexamples"].items():
            # Round-trip the serialized trace before replay; evidence is portable JSON.
            end = replay(model, json.loads(json.dumps(trace)))
            if name not in model.violations(end):
                raise RuntimeError("Counterexample did not replay: " + model.name + ": " + name)
        path = output / (model.name.replace("/", "-") + ".json")
        path.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")
        row = {key: result[key] for key in ("model", "status", "oracle_met", "states_discovered",
                                           "states_expanded", "transitions_examined", "goal_states", "expected_violation")}
        row.update(report=path.name, sha256=digest(path), progress=result["progress"]["status"],
                   counterexamples=list(result["counterexamples"]))
        cases.append(row)
        print(json.dumps(row, sort_keys=True), flush=True)
    passed = all(case["oracle_met"] for case in cases)
    exit_code = 0 if passed else 2 if any(case["status"] == "inconclusive" for case in cases) else 1
    head = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(root), "status", "--porcelain", "--", "src/Verification.Models",
                                    "tests/Verification.Models", "samples/api/verification-models",
                                    "docs/guides/verification-models.md"], text=True).strip()
    manifest = source_manifest(root)
    manifest_path = output / "source-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    summary = {
        "schema_version": "1.0.0", "task": "A19", "evidence_kind": "model_check",
        "status": "passed" if passed else "inconclusive" if exit_code == 2 else "failed",
        "scope": "Finite SC model tooling; not Native AOT, actual runtime race, domain or release acceptance.",
        "complete_suite": len(selected) == len(models()), "exit_code": exit_code,
        "source": {"repository": "IzuSharp/Korda", "baseline_commit": head,
                   "scope_has_uncommitted_changes": bool(dirty), "manifest": manifest_path.name,
                   "manifest_sha256": digest(manifest_path)},
        "inputs": design_validation, "refinement": refinement,
        "environment": {"python": platform.python_version(), "python_implementation": platform.python_implementation(),
                        "interpreter_sha256": digest(Path(sys.executable).resolve()),
                        "os": platform.system(), "architecture": platform.machine(),
                        "kernel": platform.release(), "model_checker_version": TOOL_VERSION},
        "started_utc": started, "ended_utc": utc_now(), "argv": sys.argv,
        "cwd": str(Path.cwd()), "cases": cases,
        "assumptions": "Per-model bounds/progress assumptions are in every case report; no random seeds are used.",
        "not_proven": ["Weak memory, arbitrary Rust/CLR/GC/FFI behavior or real kernel scheduling.",
                       "Unbounded liveness, permanently hung actors, all operation counts or all intermodel compositions.",
                       "Native AOT, independent NuGet/pristine consumption, real database/server execution or performance.",
                       "Windows execution; the current acceptance run is Linux x64 only."],
    }
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps({"status": summary["status"], "case_count": len(cases), "summary": str(output / "summary.json")}), flush=True)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
