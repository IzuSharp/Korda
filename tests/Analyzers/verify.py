#!/usr/bin/env python3
"""A12: exact diagnostics, compiler-only packaging and real native JIT/AOT consumers."""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
VERSION = "0.1.0-task03.a12"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    parser.add_argument("--design-root", type=Path)
    parser.add_argument("--prepare", action="store_true")
    args = parser.parse_args()
    out = (args.output or ROOT / "artifacts/task03/A12" / datetime.datetime.now(datetime.timezone.utc).strftime("run-%Y%m%dT%H%M%SZ")).resolve()
    out.mkdir(parents=True, exist_ok=False)
    scopes = ["src/Analyzers", "tests/Analyzers", "samples/api/analyzers", "docs/guides/analyzers.md", "native/hello-world"]
    tracked = subprocess.check_output(["git", "ls-files", "--", *scopes], cwd=ROOT, text=True).splitlines()
    tracked += ["Cargo.toml", "Cargo.lock", "global.json", "Directory.Build.props", "NuGet.Config", "rust-toolchain.toml"]
    inputs = {p: sha(ROOT / p) for p in sorted(set(tracked)) if (ROOT / p).is_file()}
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip())
    env = os.environ.copy()
    env.update(DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    commands, artifacts = [], {}
    passed = False
    consumer = None

    def run(label, command, *, cwd=ROOT, environment=env, expected=0, contains=()):
        started = time.monotonic()
        stdout, stderr = out / (label + ".stdout"), out / (label + ".stderr")
        with stdout.open("w") as o, stderr.open("w") as e:
            result = subprocess.run([str(x) for x in command], cwd=cwd, env=environment, stdout=o, stderr=e)
        text = stdout.read_text() + stderr.read_text()
        okay = (result.returncode == expected if expected is not None else result.returncode != 0) and all(s in text for s in contains)
        commands.append({"label": label, "command": [str(x) for x in command], "cwd": str(cwd), "returncode": result.returncode,
                         "expected": expected if expected is not None else "nonzero-with-required-diagnostics", "required_diagnostics": list(contains),
                         "passed": okay, "seconds": round(time.monotonic() - started, 3), "stdout_sha256": sha(stdout), "stderr_sha256": sha(stderr)})
        (out / "commands.json").write_text(json.dumps(commands, indent=2) + "\n")
        print(label, result.returncode, flush=True)
        if not okay:
            raise RuntimeError(label + " failed; inspect retained stdout/stderr")
        return text

    try:
        if args.design_root:
            design = args.design_root.resolve()
            contract = json.loads((ROOT / "src/Analyzers/design-inputs.json").read_text())
            assert run("design-commit", ["git", "-C", design, "rev-parse", "HEAD"]).strip() == contract["design_commit"]
            for path, digest in contract["api_inputs"].items():
                assert sha(design / path.split(":", 1)[1]) == digest, path
        for label, command in [("dotnet", ["dotnet", "--info"]), ("rustc", ["rustc", "-Vv"]), ("cargo", ["cargo", "--version"]), ("clang", ["clang", "--version"])]:
            run(label, command)
        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
        restore = [] if args.prepare else ["--locked-mode"]
        project = "tests/Analyzers/NativeWeave.Analyzers.Tests.csproj"
        run("tests-restore", ["dotnet", "restore", project, *restore, *common])
        run("tests-build", ["dotnet", "build", project, "--no-restore", "-c", "Release", *common])
        test = ROOT / "tests/Analyzers/bin/Release/net11.0/NativeWeave.Analyzers.Tests.dll"
        first = run("diagnostics", ["dotnet", test])
        second = run("diagnostics-repeat", ["dotnet", test])
        assert first == second, "same input produced different diagnostic results"
        run("compiled-api-xml", ["dotnet", test, "--api-reference", out / "api-reference.md"])
        reference = ROOT / "src/Analyzers/api-reference.md"
        if args.prepare:
            shutil.copy2(out / "api-reference.md", reference)
        else:
            assert reference.read_bytes() == (out / "api-reference.md").read_bytes(), "stale generated API reference"
        run("native-prebuilt-dependency", ["cargo", "build", "--locked", "-p", "nativeweave-hello", "--release"])
        native = ROOT / "target/release/libnativeweave_hello.so"
        source_project = "samples/api/analyzers/AnalyzersSample.csproj"
        native_property = "-p:NativeWeaveNativeLibrary=" + str(native)
        run("source-sample-restore", ["dotnet", "restore", source_project, *restore, *common])
        run("source-sample-build", ["dotnet", "build", source_project, "--no-restore", "-c", "Release", native_property, *common])
        sample = ROOT / "samples/api/analyzers/bin/Release/net11.0/NativeWeave.Analyzers.Sample.dll"
        run("source-sample-jit", ["dotnet", sample])
        run("source-sample-aot-restore", ["dotnet", "restore", source_project, "-r", "linux-x64", "-p:PublishAot=true", *restore, *common])
        source_publish = out / "source-published"
        run("source-sample-aot-publish", ["dotnet", "publish", source_project, "--no-restore", "-c", "Release", "-r", "linux-x64",
            "-p:PublishAot=true", "-o", source_publish, native_property, *common])
        run("source-sample-aot-execute", [source_publish / "NativeWeave.Analyzers.Sample", "--require-aot"])
        assert not list(source_publish.rglob("NativeWeave.Analyzers.dll")), "source analyzer leaked into AOT output"
        package_dir = out / "packages"
        run("package", ["dotnet", "pack", "src/Analyzers/NativeWeave.Analyzers.csproj", "--no-restore", "-c", "Release", "-o", package_dir, "-p:PackageVersion=" + VERSION, *common])
        package = package_dir / ("NativeWeave.Analyzers." + VERSION + ".nupkg")
        with zipfile.ZipFile(package) as archive:
            names = archive.namelist()
            assert [n for n in names if n.endswith(".dll")] == ["tools/netstandard2.0/NativeWeave.Analyzers.dll"]
            assert not any(n.startswith(("lib/", "runtimes/", "analyzers/")) for n in names)
            assert "buildTransitive/NativeWeave.Analyzers.targets" in names
            nuspec = ET.fromstring(archive.read("NativeWeave.Analyzers.nuspec"))
            assert not any(e.tag.endswith("developmentDependency") and (e.text or "").lower() == "true" for e in nuspec.iter())
            assert not any(e.tag.endswith("dependency") for e in nuspec.iter())
            (out / "package-inventory.json").write_text(json.dumps(names, indent=2) + "\n")
        consumer = Path(tempfile.mkdtemp(prefix="nativeweave-a12-consumer-"))
        for file in (ROOT / "samples/api/analyzers").iterdir():
            if file.suffix in (".cs", ".csproj"):
                shutil.copy2(file, consumer / file.name)
        shutil.copy2(ROOT / "global.json", consumer / "global.json")
        shutil.copy2(native, consumer / native.name)
        (consumer / "Directory.Build.props").write_text("<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetAudit>true</NuGetAudit></PropertyGroup></Project>\n")
        configuration = ET.Element("configuration")
        sources = ET.SubElement(configuration, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="candidate", value=str(package_dir))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        ET.ElementTree(configuration).write(consumer / "NuGet.Config", encoding="utf-8", xml_declaration=True)
        traps = consumer / "rust-traps"
        traps.mkdir()
        for name in ("cargo", "rustc", "rustup"):
            trap = traps / name
            trap.write_text("#!/bin/sh\nprintf '%s\\n' 'unexpected Rust invocation' >> '" + str(consumer / "rust-invoked.txt") + "'\nexit 91\n")
            trap.chmod(0o755)
        consumer_env = dict(env, NUGET_PACKAGES=str(consumer / "packages"), PATH=str(traps) + os.pathsep + env["PATH"])
        properties = ["-p:NativeWeaveAnalyzerPackageVersion=" + VERSION, "-p:NativeWeaveNativeLibrary=" + str(consumer / native.name), *common]
        run("package-consumer-restore", ["dotnet", "restore", "AnalyzersSample.csproj", *properties], cwd=consumer, environment=consumer_env)
        run("package-consumer-build", ["dotnet", "build", "AnalyzersSample.csproj", "--no-restore", "-c", "Release", *properties], cwd=consumer, environment=consumer_env)
        run("package-consumer-jit", ["dotnet", consumer / "bin/Release/net11.0/NativeWeave.Analyzers.Sample.dll"], cwd=consumer, environment=consumer_env)
        items = run("package-analyzer-items", ["dotnet", "msbuild", "AnalyzersSample.csproj", "-getItem:Analyzer", *properties], cwd=consumer, environment=consumer_env)
        inventory = json.loads(items[items.index("{"):])["Items"]["Analyzer"]
        assert sum(Path(item["Identity"]).name == "NativeWeave.Analyzers.dll" for item in inventory) == 1
        assert not list((consumer / "bin").rglob("NativeWeave.Analyzers.dll")), "compiler plug-in leaked into runtime output"
        clean_source = (consumer / "NativeMethods.cs").read_text()
        (consumer / "NativeMethods.cs").write_text(clean_source.replace("partial uint Version", "partial bool Version"))
        run("package-activation-negative", ["dotnet", "build", "AnalyzersSample.csproj", "--no-restore", "-c", "Release", *properties], cwd=consumer, environment=consumer_env, expected=None, contains=("NWABI001",))
        (consumer / "NativeMethods.cs").write_text(clean_source)
        (consumer / "NewDto.cs").write_text("namespace Sample; internal sealed record NewDto(int Id, string Name);\n")
        run("package-consumer-aot-restore", ["dotnet", "restore", "AnalyzersSample.csproj", "-r", "linux-x64", "-p:PublishAot=true", *properties], cwd=consumer, environment=consumer_env)
        destination = consumer / "published"
        run("package-consumer-aot-publish", ["dotnet", "publish", "AnalyzersSample.csproj", "--no-restore", "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "-o", destination, *properties], cwd=consumer, environment=consumer_env)
        executable = destination / "NativeWeave.Analyzers.Sample"
        run("package-consumer-aot-execute", [executable, "--require-aot"], cwd=consumer, environment=consumer_env)
        assert not list(destination.rglob("NativeWeave.Analyzers.dll")), "analyzer payload leaked into AOT output"
        assert sha(native) == sha(consumer / native.name) == sha(destination / native.name), "managed edit rebuilt or changed the native asset"
        run("framework-aot-warning-negative", ["dotnet", "publish", "AnalyzersSample.csproj", "--no-restore", "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "-p:NativeWeaveAotNegative=true", "-o", consumer / "negative-publish", *properties], cwd=consumer, environment=consumer_env, expected=None, contains=("IL2026", "IL3050"))
        assert not (consumer / "rust-invoked.txt").exists(), "ordinary managed consumption invoked Rust"
        for path in [native, package, ROOT / "src/Analyzers/bin/Release/netstandard2.0/NativeWeave.Analyzers.dll", test, sample, executable,
                     source_publish / "NativeWeave.Analyzers.Sample",
                     consumer / "packages.lock.json", consumer / "packages.linux-x64.aot.lock.json"]:
            artifacts[str(path)] = sha(path)
        shutil.copytree(consumer, out / "consumer-inputs", ignore=shutil.ignore_patterns("bin", "obj", "packages", "published", "negative-publish", "rust-traps", "*.so"))
        (out / "consumer.json").write_text(json.dumps({"path": str(consumer), "package_version": VERSION, "pristine_machine": False,
            "isolated_package_cache": True, "rust_trap_invocations": 0, "analyzer_item_count": 1, "compiler_payload_in_runtime_output": False,
            "native_sha256_before_after_managed_edit": sha(native), "aot_executed": True, "framework_diagnostics_retained": ["IL2026", "IL3050"]}, indent=2) + "\n")
        stable = all((ROOT / path).is_file() and sha(ROOT / path) == digest for path, digest in inputs.items())
        passed = stable and not dirty and not args.prepare
        if not args.prepare and not passed:
            raise RuntimeError("Formal evidence requires clean unchanged committed inputs.")
    finally:
        (out / "result.json").write_text(json.dumps({"task": "A12", "platform": "linux-x64", "passed": passed,
            "preparation_only": args.prepare, "source_commit": head, "source_dirty": dirty, "source_sha256": inputs,
            "source_stable": all((ROOT / path).is_file() and sha(ROOT / path) == digest for path, digest in inputs.items()),
            "commands": commands, "artifact_sha256": artifacts, "consumer_path": str(consumer) if consumer else None,
            "independent_review": "pending", "limitations": ["Linux x64 and pinned CLI compiler host only", "No IDE/Windows/pristine-machine claim",
            "Component diagnostic checks are not an unsafe-code proof", "No new publication or complete domain-host acceptance"]}, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
