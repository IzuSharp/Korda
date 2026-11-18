#!/usr/bin/env python3
"""Execute transferred B24 AOT outputs in the existing runtime-only Linux image."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

RUNTIME_IMAGE = "mcr.microsoft.com/dotnet/runtime-deps:10.0.15-noble-amd64@sha256:ebb73a172b49758c6de2462305e872ea0cb37ad3ca27cc0d86bc48b6271eaace"
PROVIDERS = {"sqlite": ["sqlite"], "postgresql": ["postgresql"], "server": ["none"],
             "grpc": ["none"], "mcp": ["none"], "combined": ["sqlite", "postgresql"]}


def required(graph):
    result = {"actual-native-aot"}
    if graph in ("sqlite", "postgresql", "combined"):
        result.update(("typed-entity-materialization", "typed-native-database-roundtrip", "database-local-owners-retired"))
    if graph in ("server", "grpc", "mcp", "combined"):
        result.update(("actual-http-1.1-tls", "actual-http-2.0-tls", "server-owners-retired"))
    if graph in ("grpc", "combined"):
        result.add("actual-grpc-http2-terminal-status-and-result")
    if graph in ("mcp", "combined"):
        result.add("actual-mcp-typed-result")
    if graph == "combined":
        result.update(("ordinary-added-entity-dto-handler", "ordinary-added-http-handler"))
    return result


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def relative(root, name):
    if not name or "\\" in name or ":" in name or Path(name).is_absolute() or any(part in ("", ".", "..") for part in name.split("/")):
        raise ValueError("Invalid transferred relative path")
    result = root / name
    if not result.resolve().is_relative_to(root.resolve()):
        raise ValueError("Transfer symlink escapes the runtime bundle")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    bundle, output = args.bundle.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    if os.environ.get("B24_RUNTIME_IMAGE") != RUNTIME_IMAGE:
        raise ValueError("Runtime execution requires the existing pinned runtime-only image")
    absence = {tool: shutil.which(tool) is None for tool in ("dotnet", "cargo", "rustc", "rustup", "clang", "cc")}
    if not all(absence.values()):
        raise ValueError("Runtime-only image contains a managed/native build tool")
    manifest = json.loads((bundle / "manifest.json").read_text())
    if manifest.get("rid") != "linux-x64" or manifest.get("task") != "B24" or set(manifest.get("graphs", {})) != {"sqlite", "postgresql", "server", "grpc", "mcp", "combined"}:
        raise ValueError("Incomplete actual B24 AOT transfer")
    result = {"schemaVersion": 1, "task": "B24", "passed": False, "image": RUNTIME_IMAGE,
              "source_commit": manifest["source_commit"], "packages": manifest["packages"],
              "manifestSha256": sha(bundle / "manifest.json"), "toolAbsence": absence, "cases": []}
    work = Path(tempfile.mkdtemp(prefix="nativeweave-b24-runtime-"))
    try:
        for graph, row in manifest["graphs"].items():
            if row.get("providers") != PROVIDERS[graph]:
                raise ValueError("Transferred graph omits or changes required provider rows")
            directory = relative(bundle, graph)
            for name, digest in row["files"].items():
                if sha(relative(directory, name)) != digest:
                    raise ValueError("Transferred artifact identity differs")
            executable = relative(bundle, row["executable"])
            if executable.parent != directory or executable.name not in row["files"]:
                raise ValueError("Transferred executable is not bound by its graph inventory")
            if row.get("aotElfAudit", {}).get("sha256") != sha(executable) or row["aotElfAudit"].get("executable") is not True:
                raise ValueError("Transferred executable lacks its bound Linux profile audit")
            for provider in row["providers"]:
                env = os.environ.copy()
                env.pop("LD_LIBRARY_PATH", None)
                env["B24_PROVIDER"] = provider
                if provider == "postgresql":
                    env["B24_DATABASE"] = env.get("NW_TEST_POSTGRES") or ""
                    if not env["B24_DATABASE"]:
                        raise ValueError("Real PostgreSQL requires NW_TEST_POSTGRES in the runtime image")
                elif provider == "sqlite":
                    env["B24_DATABASE"] = str(work / (graph + ".sqlite.db"))
                else:
                    env.pop("B24_DATABASE", None)
                process = subprocess.run([str(executable), "--require-aot"], cwd=work, env=env, capture_output=True, timeout=120)
                text = process.stdout.decode(errors="replace")
                receipts = [json.loads(line[len("B24_RESULT "):]) for line in text.splitlines() if line.startswith("B24_RESULT ")]
                if process.returncode or len(receipts) != 1 or receipts[0].get("dynamicCodeSupported") is not False:
                    raise ValueError("Transferred AOT domain process did not pass")
                receipt = receipts[0]
                expected = set(row["expectedChecks"]) | required(graph)
                if receipt.get("task") != "B24" or receipt.get("provider") != provider or not expected.issubset(receipt.get("checks", [])):
                    raise ValueError("Transferred process omitted required domain operations")
                result["cases"].append({"graph": graph, "provider": provider, "passed": True,
                    "processExited": True, "executableSha256": sha(executable), "receipt": receipt,
                    "stdoutSha256": hashlib.sha256(process.stdout).hexdigest(), "stderrSha256": hashlib.sha256(process.stderr).hexdigest()})
                print(graph, provider, "passed", flush=True)
            for name, digest in row["files"].items():
                if sha(relative(directory, name)) != digest:
                    raise ValueError("Transferred artifact changed during actual execution")
        result["passed"] = True
    finally:
        (output / "receipt.json").write_text(json.dumps(result, indent=2, sort_keys=True) + "\n")


if __name__ == "__main__":
    main()
