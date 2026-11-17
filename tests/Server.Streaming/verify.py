#!/usr/bin/env python3
"""Execute B16 Linux x64 native Body/ownership and compiled managed JIT/AOT checks."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        parser.error("This execution profile is Linux x64 only.")
    output = (args.output or ROOT / "artifacts/task06/B16" / time.strftime("run-%Y%m%dT%H%M%SZ", time.gmtime())).resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    env["LD_LIBRARY_PATH"] = str(ROOT / "target/release") + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    report = {"task": "B16", "platform": "linux-x64", "passed": False, "commands": [], "artifacts": {},
              "scope": "Production streaming component plus explicit native/Core-operation fixture; no HTTP/TLS Host or other-platform acceptance.",
              "distribution": "B16 sources compose into NativeWeave.Server; no independent B16 package or consumer Cargo target.",
              "offline_audit_not_refreshed": args.offline}

    def run(label, argv):
        result = subprocess.run(list(map(str, argv)), cwd=ROOT, env=env, capture_output=True, timeout=300)
        for suffix, data in (("stdout", result.stdout), ("stderr", result.stderr)):
            (output / (label + "." + suffix)).write_bytes(data)
        report["commands"].append({"label": label, "argv": list(map(str, argv)), "exit": result.returncode,
                                  "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(),
                                  "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        print(label, result.returncode, flush=True)
        if result.returncode:
            raise RuntimeError(label + ": " + result.stdout.decode(errors="replace")[-4000:] + result.stderr.decode(errors="replace")[-4000:])
        return result.stdout.decode(errors="replace")

    try:
        report["source_commit"] = run("commit", ["git", "rev-parse", "HEAD"]).strip()
        report["source_dirty"] = bool(run("status", ["git", "status", "--porcelain"]).strip())
        if report["source_dirty"]:
            raise RuntimeError("Commit the component before formal verification.")
        for label, argv in (("dotnet", ["dotnet", "--version"]), ("rustc", ["rustc", "-vV"]),
                            ("cargo", ["cargo", "--version"]), ("clang", ["clang", "--version"])):
            run(label, argv)
        scopes = ["src/Server.Streaming", "native/server/streaming", "tests/Server.Streaming", "samples/api/server-streaming",
                  "docs/guides/server-streaming.md", "src/NativeWeave.Core", "src/Interop.Abi", "src/Interop.Lifetime",
                  "src/Interop.Async", "src/Interop.Callbacks", "src/Interop.Errors", "src/Interop.Negotiation",
                  "native/interop/abi", "native/interop/async", "native/interop/errors", "Cargo.toml", "Cargo.lock",
                  "Directory.Build.props", "Directory.Build.targets", "global.json", "rust-toolchain.toml"]
        paths = run("source-files", ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *scopes]).splitlines()
        report["source_sha256"] = {path: sha(ROOT / path) for path in sorted(set(paths)) if (ROOT / path).is_file()}
        offline = ["--offline"] if args.offline else []
        run("native-format", ["cargo", "fmt", "-p", "nativeweave-server-streaming", "--", "--check"])
        run("native-clippy", ["cargo", "clippy", "--locked", *offline, "-p", "nativeweave-server-streaming", "--all-targets", "--all-features", "--", "-D", "warnings"])
        run("native-tests", ["cargo", "test", "--release", "--locked", *offline, "-p", "nativeweave-server-streaming"])
        run("native-default", ["cargo", "build", "--release", "--locked", *offline, "-p", "nativeweave-server-streaming"])
        native = ROOT / "target/release/libnativeweave_server_streaming.so"
        symbols = run("default-exports", ["nm", "-D", "--defined-only", native])
        if any("nw_streaming_fixture_" in line for line in symbols.splitlines()):
            raise RuntimeError("Default production build exports conformance entry points.")
        report["artifacts"]["default_native"] = {"path": str(output / "libnativeweave_server_streaming.default.so"), "sha256": sha(native)}
        shutil.copyfile(native, output / "libnativeweave_server_streaming.default.so")
        run("native-fixture", ["cargo", "build", "--release", "--locked", *offline, "-p", "nativeweave-server-streaming", "--features", "conformance"])
        shutil.copyfile(native, output / native.name)
        report["artifacts"]["native_fixture"] = {"path": str(output / native.name), "sha256": sha(native)}
        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
        restore = ["--locked-mode"] + (["-p:NuGetAudit=false", "--ignore-failed-sources"] if args.offline else [])
        project = "tests/Server.Streaming/Server.Streaming.Tests.csproj"
        run("managed-restore", ["dotnet", "restore", project, *restore, *common])
        run("managed-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
        dll = ROOT / "tests/Server.Streaming/bin/Release/net11.0/NativeWeave.Server.Streaming.Tests.dll"
        report["jit"] = run("jit", ["dotnet", dll]).strip()
        report["artifacts"]["jit"] = {"path": str(dll), "sha256": sha(dll)}
        run("aot-restore", ["dotnet", "restore", project, "-r", "linux-x64", "-p:PublishAot=true", *restore, *common])
        publish = output / "aot"
        run("aot-publish", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true",
                            "-p:IlcTreatWarningsAsErrors=true", "--no-restore", "-o", publish, *common])
        executable = publish / "NativeWeave.Server.Streaming.Tests"
        if (publish / "NativeWeave.Server.Streaming.Tests.dll").exists(): raise RuntimeError("Managed apphost is not Native AOT.")
        report["aot"] = run("aot-run", [executable, "--require-aot"]).strip()
        report["artifacts"]["aot"] = {"path": str(executable), "sha256": sha(executable)}
        api = "tests/Server.Streaming/ApiCheck/ApiCheck.csproj"
        run("api-restore", ["dotnet", "restore", api, *restore, *common])
        run("api-build", ["dotnet", "build", api, "-c", "Release", "--no-restore", *common])
        sdk = Path(shutil.which("dotnet")).resolve().parent
        report["api"] = run("api-xml-reference", ["dotnet", ROOT / "tests/Server.Streaming/ApiCheck/bin/Release/net11.0/NativeWeave.Server.Streaming.ApiCheck.dll", ROOT, sdk, dll]).strip()
        run("diff-check", ["git", "diff", "--check"])
        for path, digest in report["source_sha256"].items():
            if sha(ROOT / path) != digest: raise RuntimeError("Source changed during verification: " + path)
        report["source_unchanged"] = True
        report["passed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n")
        print("Evidence:", output, flush=True)


if __name__ == "__main__":
    main()
