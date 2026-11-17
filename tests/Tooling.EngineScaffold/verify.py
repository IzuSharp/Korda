#!/usr/bin/env python3
"""Generate one reviewed real Engine profile and run its installed conformance entrypoint.

Run once per A17/A18 descriptor. Neither a descriptor nor a passing validation-only
fixture is real Engine acceptance; the generated package must execute JIT and AOT.
"""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("engine_scaffold", ROOT / "src/Tooling.EngineScaffold/scaffold.py")
scaffold = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scaffold)


def common_sources():
    names = subprocess.check_output(["git", "ls-files", "--", "src/NativeWeave.Core", "src/Interop.*", "native/interop"],
                                    cwd=ROOT, text=True).splitlines()
    return {name: scaffold.digest(ROOT / name) for name in names}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--descriptor", type=Path, required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sdk-revision", required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT) or output.exists():
        raise ValueError("Use a fresh output inside this maintainer checkout for root-contained negative inputs")
    output.mkdir(parents=True)
    before = common_sources()
    receipt = {"task": "A24", "platform": "linux-x64", "status": "running", "pristine_machine": False,
               "source_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "descriptor_sha256": scaffold.digest(args.descriptor), "negative_cases": [], "common_before": before}
    try:
        descriptor = scaffold.load(ROOT, args.descriptor)
        for label, mutation in (("unsupported-schema", lambda d: d.update(schema_version=999)),
                                ("unknown-required-component", lambda d: d["components"].append("unknown-feature"))):
            changed = copy.deepcopy(descriptor)
            mutation(changed)
            path = output / (label + ".json")
            path.write_text(json.dumps(changed, indent=2) + "\n")
            try:
                scaffold.load(ROOT, path)
            except scaffold.ScaffoldError as error:
                receipt["negative_cases"].append({"case": label, "rejected": True, "diagnostic": str(error)})
            else:
                raise AssertionError(label + " was accepted")
        # Drive the actual A09/A10 validator with a contradictory native record
        # size, retaining the original parsed/linked inventories and adapter.
        changed = copy.deepcopy(descriptor)
        sidecar = json.loads(scaffold.checked_file(ROOT, changed["binding"]["sidecar"]).read_text())
        sidecar["records"][0]["size"] += 8
        altered = output / "unsupported-layout-sidecar.json"
        altered.write_text(json.dumps(sidecar, indent=2) + "\n")
        changed["binding"]["sidecar"] = {"path": altered.relative_to(ROOT).as_posix(), "sha256": scaffold.digest(altered)}
        path = output / "unsupported-layout.json"
        path.write_text(json.dumps(changed, indent=2) + "\n")
        try:
            scaffold.generate(ROOT, path, output / "invalid-layout", args.sdk_revision)
        except scaffold.ScaffoldError as error:
            failures = list(output.glob("invalid-layout.failed-*/artifacts/generation/csharp.stderr"))
            if not failures or not failures[0].read_text().strip():
                raise AssertionError("Layout negative did not reach the native binding validator") from error
            receipt["negative_cases"].append({"case": "contradictory-native-layout", "rejected": True,
                "diagnostic": failures[0].read_text(), "stderr_sha256": scaffold.digest(failures[0])})
        else:
            raise AssertionError("Contradictory native layout was accepted")
        generated = output / "generated"
        receipt["generation"] = scaffold.generate(ROOT, args.descriptor, generated, args.sdk_revision)
        command = [sys.executable, str(generated / "eng/verify.py"), "--feed", str(args.feed.resolve()), "--sdk-root", str(ROOT)]
        completed = subprocess.run(command, cwd=generated, capture_output=True, timeout=1800)
        for suffix, data in (("stdout", completed.stdout), ("stderr", completed.stderr)):
            (output / ("conformance." + suffix)).write_bytes(data)
        receipt["conformance"] = {"exit_code": completed.returncode,
            "stdout_sha256": scaffold.digest(output / "conformance.stdout"),
            "stderr_sha256": scaffold.digest(output / "conformance.stderr")}
        if completed.returncode:
            raise RuntimeError("Generated conformance failed; inspect retained output")
        result_path = generated / "artifacts/conformance/result.json"
        result = json.loads(result_path.read_text())
        if result["status"] != "passed":
            raise AssertionError("Generated conformance did not pass")
        receipt["package_receipt_sha256"] = scaffold.digest(result_path)
        receipt["package_result"] = result
        after = common_sources()
        receipt["core_source_changed"] = before != after
        if before != after:
            raise AssertionError("A scaffold modified shared Core source")
        receipt.update(status="passed", engine_id=descriptor["engine_id"], components=descriptor["components"],
                       threading=descriptor["threading"])
    except BaseException as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        (output / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps({"status": receipt["status"], "receipt": str(output / "result.json")}))


if __name__ == "__main__":
    main()
