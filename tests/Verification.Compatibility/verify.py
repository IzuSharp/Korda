#!/usr/bin/env python3
"""Linux A22 component evidence. No publishing, deletion, unlisting or auto-update."""
from __future__ import annotations
import argparse
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import time
import zipfile
import xml.etree.ElementTree as ET

from test_policy import ROOT, compat, record, run as policy_cases, write


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--native-build", type=Path, default=ROOT / "artifacts/cmake/linux-x64-release")
    parser.add_argument("--api-tool-root", type=Path, default=ROOT)
    parser.add_argument("--design-root", type=Path)
    parser.add_argument("--prepare", action="store_true")
    args = parser.parse_args()
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    if (evidence / "result.json").exists():
        raise SystemExit("Use a new evidence directory; previous receipts are immutable.")
    assert platform.system() == "Linux" and platform.machine() == "x86_64"
    started = time.time()
    source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    initial_status = subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True)
    if not args.prepare and initial_status:
        raise SystemExit("Formal evidence requires a committed clean source tree. Use --prepare only for development.")
    commands, hashes, matrix = [], {}, []
    def run(name, command, *, cwd=ROOT, expected=0, contains=(), env=None):
        command = [str(x) for x in command]
        began = time.time()
        result = subprocess.run(command, cwd=cwd, text=True, capture_output=True, env=env, timeout=360)
        (evidence / f"{name}.stdout").write_text(result.stdout)
        (evidence / f"{name}.stderr").write_text(result.stderr)
        passed = (result.returncode == expected if expected is not None else result.returncode != 0)
        passed = passed and all(text in result.stdout + result.stderr for text in contains)
        commands.append({"name": name, "command": command, "cwd": str(cwd), "exit_code": result.returncode,
                         "expected_exit": expected, "passed": passed, "seconds": round(time.time()-began, 3)})
        write(evidence / "commands.json", commands)
        if not passed:
            raise AssertionError(f"{name} failed; see retained stdout/stderr")
        return result.stdout

    common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
    locked = [] if args.prepare else ["--locked-mode"]
    sample = ROOT / "samples/api/verification-compatibility/CompatibilitySample.csproj"
    try:
        design = args.design_root or next((p for p in [ROOT.parent / "NativeWeave-Design", ROOT.parents[2] / "NativeWeave-Design"] if p.is_dir()), None)
        assert design is not None, "Use --design-root for the locked read-only Design checkout"
        design_inputs = compat.read_json(ROOT / "src/Verification.Compatibility/design-inputs.json")
        design_commit = subprocess.check_output(["git", "-C", str(design), "rev-parse", "HEAD"], text=True).strip()
        subprocess.run(["git", "-C", str(design), "merge-base", "--is-ancestor", design_inputs["design_commit"], design_commit], check=True)
        scope_commit = subprocess.check_output(["git", "-C", str(design), "rev-parse", "8298dc4^{commit}"], text=True).strip()
        subprocess.run(["git", "-C", str(design), "merge-base", "--is-ancestor", scope_commit, design_commit], check=True)
        removed_by_database_scope = {
            "docs/api/server.md", "docs/api/server-coverage.json", "docs/api/server-sources.json",
            "docs/api/server-symbol-policies.json", "docs/server/body-retirement-protocol.md"}
        design_checks = {}
        for name, expected in design_inputs["inputs"].items():
            relative = name.split(":", 1)[1]
            historical = subprocess.check_output(["git", "-C", str(design), "show", design_inputs["design_commit"] + ":" + relative])
            assert hashlib.sha256(historical).hexdigest() == expected, name
            path = design / relative
            if not path.exists():
                assert relative in removed_by_database_scope, relative
                # This removal must already exist in the fixed DB-Scope-01
                # Design baseline, not be excused by a candidate checkout.
                present = subprocess.run(["git", "-C", str(design), "cat-file", "-e", scope_commit + ":" + relative], capture_output=True)
                assert present.returncode != 0, relative
                design_checks[name] = {"historical_sha256": expected, "current": "removed-in-DB-Scope-01"}
            else:
                current = compat.file_hash(path)
                assert current == expected or path.suffix == ".md", name
                design_checks[name] = {"historical_sha256": expected, "current_sha256": current}
        write(evidence / "design-inputs.json", {"commit": design_commit, "historical_commit": design_inputs["design_commit"],
            "checked_inputs": design_checks, "manifest_sha256": compat.file_hash(ROOT / "src/Verification.Compatibility/design-inputs.json")})
        run("dotnet-version", ["dotnet", "--info"])
        run("native-toolchain", ["clang++", "--version"])
        build = args.native_build.resolve()
        provenance = build / "compiler-provenance.txt"
        assert provenance.is_file() and "\nID=Clang\nVERSION=23.1.5\n" in provenance.read_text()
        (evidence / "compiler-provenance.txt").write_bytes(provenance.read_bytes())
        fixture_source = ROOT / "native/cpp/tests/fixtures/compatibility.cpp"
        run("native-format", ["clang-format", "--dry-run", "--Werror", fixture_source, ROOT / "native/cpp/tests/compatibility_tests.cpp"])
        natives = {}
        for name in ("old", "new", "major", "layout", "semantics"):
            built = build / "fixtures" / ("libnw_compat_" + name + ".so")
            assert built.is_file(), "Build every nw_compat_* CMake target before compatibility verification."
            out = evidence / "native" / name / "libnw_compat.so"
            out.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(built, out)
            natives[name] = out
            hashes[f"native-{name}"] = record(out, ROOT)
            run(f"native-symbols-{name}", ["nm", "-D", "--defined-only", out])
            if name in ("old", "new", "semantics"):
                test = build / "tests" / ("nw_compatibility_" + name + "_tests")
                run(f"native-direct-{name}", [test], expected=None if name == "semantics" else 0,
                    contains=("FAILED",) if name == "semantics" else ("C++ direct oracle passed",))

        run("sample-restore", ["dotnet", "restore", sample, *locked, *common])
        managed = {}
        for generation in ("old", "new"):
            output = evidence / "managed" / generation
            run(f"managed-{generation}", ["dotnet", "build", sample, "--no-restore", "-c", "Release", "-o", output,
                f"-p:CompatibilityGeneration={generation}", *common])
            managed[generation] = output / "NativeWeave.Compatibility.Sample.dll"
            hashes[f"managed-{generation}"] = record(managed[generation], ROOT)
        def matrix_rows(mode, consumers):
            for generation in ("old", "new"):
                for native_generation in ("old", "new"):
                    native = natives[native_generation]
                    command = (["dotnet", consumers[generation]] if mode == "jit" else [consumers[generation]])
                    output = run(f"{mode}-{generation}-managed-{native_generation}-native", [*command, native, compat.file_hash(native), "pass", *([] if mode == "jit" else ["--require-aot"])])
                    row = json.loads(output.strip().splitlines()[-1]); row.update(mode=mode, native=native_generation,
                        managed_profile=record(managed[generation], ROOT), managed_artifact=record(consumers[generation], ROOT), native_artifact=record(native, ROOT))
                    matrix.append(row)
            for case, native_name in [("major", "major"), ("layout", "layout"), ("capability", "old"), ("hash", "new"), ("semantics", "semantics"), ("conflict", "new")]:
                native = natives[native_name]
                command = ["dotnet", consumers["new"]] if mode == "jit" else [consumers["new"]]
                output = run(f"{mode}-negative-{case}", [*command, native, "0" * 64 if case == "hash" else compat.file_hash(native), case,
                    *([] if mode == "jit" else ["--require-aot"])])
                row = json.loads(output.strip().splitlines()[-1]); row.update(mode=mode, native=native_name)
                matrix.append(row)
        matrix_rows("jit", managed)
        run("sample-aot-restore", ["dotnet", "restore", sample, "-r", "linux-x64", "-p:PublishAot=true", *locked, *common])
        aot = {}
        for generation in ("old", "new"):
            output = evidence / "aot" / generation
            run(f"sample-aot-{generation}", ["dotnet", "publish", sample, "--no-restore", "-c", "Release", "-r", "linux-x64",
                "-p:PublishAot=true", f"-p:CompatibilityGeneration={generation}", "-o", output, *common])
            aot[generation] = output / "NativeWeave.Compatibility.Sample"
            hashes[f"aot-{generation}"] = record(aot[generation], ROOT)
        matrix_rows("aot", aot)
        matrix_record = {"passed": True, "source_commit": source_commit, "rid": "linux-x64", "rows": matrix,
            "scope": "Prior accepted A01/A02 source components and current controlled native profiles, not released Core packages"}
        write(evidence / "matrix.json", matrix_record)

        api_tool_project = args.api_tool_root.resolve() / "src/Build.Integration/ApiReference/ApiReference.csproj"
        api_tool_source_before = compat.file_hash(api_tool_project.parent / "Program.cs")
        run("api-tool-build", ["dotnet", "build", api_tool_project, "-c", "Release", *common])
        api_tool = api_tool_project.parent / "bin/Release/net11.0/ApiReference.dll"
        # Respect the tool's actual assembly name if it differs from the project basename.
        if not api_tool.exists():
            api_tool = next(p for p in api_tool.parent.glob("*.dll") if p.with_suffix(".runtimeconfig.json").exists())
        api_tool_inputs = {"project_path": str(api_tool_project), "project_sha256": compat.file_hash(api_tool_project),
            "source_path": str(api_tool_project.parent / "Program.cs"),
            "source_sha256": compat.file_hash(api_tool_project.parent / "Program.cs"),
            "assembly_sha256": compat.file_hash(api_tool)}
        assert api_tool_inputs["source_sha256"] == api_tool_source_before, "Shared API tool changed while compiling"
        write(evidence / "a13-tool.json", api_tool_inputs)
        dotnet_root = Path(os.environ["DOTNET_ROOT"])
        api_project = ROOT / "tests/Verification.Compatibility/ApiFixture/ApiFixture.csproj"
        consumer_project = ROOT / "tests/Verification.Compatibility/ApiConsumer/ApiConsumer.csproj"
        run("api-fixture-restore", ["dotnet", "restore", api_project, *locked, *common])
        run("api-consumer-restore", ["dotnet", "restore", consumer_project, *locked, *common])
        api_snapshots, fixture_dlls = {}, {}
        for generation in ("OLD", "NEW", "DEFAULT", "NULLABILITY", "GENERIC", "REMOVED", "OBSOLETE", "AMBIGUOUS", "BEHAVIOR"):
            output = evidence / "api" / generation.lower()
            run(f"api-{generation.lower()}-build", ["dotnet", "build", api_project, "--no-restore", "-c", "Release", "-o", output,
                f"-p:FixtureGeneration={generation}", *common])
            dll = output / "NativeWeave.Compatibility.ApiFixture.dll"
            fixture_dlls[generation] = dll
            config = write(output / "config.json", {"assembly": str(dll), "sources": [str(api_project.parent)], "baselines": [],
                "title": "A22 controlled compiled API evolution fixture", "scope": "Unpublished compatibility fixture; not a product API",
                "output": str(output / "reference.md"), "guides": ["docs/guides/verification-compatibility.md"]})
            run(f"api-{generation.lower()}-snapshot", ["dotnet", api_tool, ROOT, dotnet_root, config, "--snapshot"])
            api_snapshots[generation] = output / "reference.json"
        api_results = []
        for generation in fixture_dlls:
            try:
                result = compat.compare_api(compat.read_json(api_snapshots["OLD"]), compat.read_json(api_snapshots[generation]))
                assert generation in ("OLD", "NEW", "AMBIGUOUS", "BEHAVIOR")
                api_results.append({"generation": generation, "snapshot": "retained", **result})
            except compat.Rejected as error:
                assert generation in ("DEFAULT", "NULLABILITY", "GENERIC", "REMOVED", "OBSOLETE") and "NWCMP006:" in str(error)
                api_results.append({"generation": generation, "snapshot": "rejected", "diagnostic": str(error)})
        binaries = {}
        for generation in ("OLD", "NEW", "DEFAULT", "NULLABILITY", "REMOVED", "OBSOLETE", "AMBIGUOUS"):
            output = evidence / "api-consumers" / generation.lower()
            negative = {"NULLABILITY": "CS8625", "REMOVED": "CS0117", "OBSOLETE": "NWCOMP001", "AMBIGUOUS": "CS0121"}.get(generation)
            run(f"source-{generation.lower()}", ["dotnet", "build", consumer_project, "--no-restore", "-c", "Release", "-o", output,
                f"-p:FixtureAssembly={fixture_dlls[generation]}", *common], expected=None if negative else 0, contains=(negative,) if negative else ())
            if negative is None:
                binaries[generation] = output / "NativeWeave.Compatibility.ApiConsumer.dll"
                run(f"source-run-{generation.lower()}", ["dotnet", binaries[generation]], expected=None if generation == "DEFAULT" else 0,
                    contains=("compatibility changed",) if generation == "DEFAULT" else ("consumer passed",))
        migrated = evidence / "api-consumers" / "migrated"
        run("obsolete-migration-build", ["dotnet", "build", consumer_project, "--no-restore", "-c", "Release", "-o", migrated,
            f"-p:FixtureAssembly={fixture_dlls['OBSOLETE']}", "-p:ConsumerGeneration=MIGRATED", *common])
        run("obsolete-migration-run", ["dotnet", migrated / "NativeWeave.Compatibility.ApiConsumer.dll"], contains=("consumer passed",))
        original_consumer = binaries["OLD"].parent
        for generation in ("NEW", "DEFAULT", "REMOVED", "BEHAVIOR"):
            output = evidence / "binary-consumers" / generation.lower()
            shutil.copytree(original_consumer, output)
            shutil.copy2(fixture_dlls[generation], output / fixture_dlls[generation].name)
            negative = {"REMOVED": "MissingMethodException", "BEHAVIOR": "compatibility changed"}.get(generation)
            run(f"binary-{generation.lower()}", ["dotnet", output / binaries["OLD"].name], expected=None if negative else 0,
                contains=(negative,) if negative else ("consumer passed",))
        write(evidence / "api-cases.json", api_results)

        # Compile the actual product Core and its existing exact EngineLoadOptions policy.
        core_dll = managed["new"].parent / "NativeWeave.Core.dll"
        core_config = write(evidence / "core-api-config.json", {"assembly": str(core_dll),
            "sources": [str(ROOT / ("src/Interop." + name)) for name in ("Negotiation", "Errors", "Lifetime", "Memory", "Callbacks", "Async", "Execution")], "baselines": [],
            "title": "Actual Core API used by A22", "scope": "Product component API; no new A22 runtime surface",
            "output": str(evidence / "core-reference.md"), "guides": ["docs/guides/verification-compatibility.md"]})
        run("core-api-snapshot", ["dotnet", api_tool, ROOT, dotnet_root, core_config, "--snapshot"])
        xml_cases = xml_documentation_negatives(run, evidence, core_dll, core_config, api_tool, dotnet_root)
        current = compat.read_json(evidence / "core-reference.json")
        baseline = compat.read_json(ROOT / "src/Interop.Negotiation/public-api.expected.json")
        select = lambda x: {"symbols": [s for s in x["symbols"] if "NativeWeave.Interop.EngineLoadOptions" in s["id"]
            and (s["id"].startswith("T:") or s["id"].startswith("P:") or s["id"].startswith("M:NativeWeave.Interop.EngineLoadOptions."))]}
        checked = compat.compare_api(select(baseline), select(current))
        assert checked["retained"] == 4 and not checked["added"]
        write(evidence / "engine-load-options-api.json", checked)
        # Both controlled driver generations use the actual existing Core API.
        # The independent ApiFixture evolution negatives do not describe Core.
        old_core = managed["old"].parent / "NativeWeave.Core.dll"
        assert compat.file_hash(old_core) == compat.file_hash(core_dll)
        matrix_record["api"] = {"previous_assembly_sha256": compat.file_hash(old_core), "candidate_assembly_sha256": compat.file_hash(core_dll),
            "previous_snapshot_sha256": compat.file_hash(evidence / "core-reference.json"),
            "candidate_snapshot_sha256": compat.file_hash(evidence / "core-reference.json")}
        write(evidence / "matrix.json", matrix_record)
        policies = policy_cases(ROOT, evidence, managed["old"], managed["new"], natives["old"], natives["new"],
                                evidence / "core-reference.json", evidence / "core-reference.json", {"source_commit": source_commit})
        closure_cases = local_closure(run, evidence, common)
        for path in [ROOT / "native/dependencies.lock.json", fixture_source, sample.parent / "Program.cs", core_dll,
                     *api_snapshots.values()]:
            hashes[str(path.relative_to(ROOT))] = {"sha256": compat.file_hash(path)}
        final_status = subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True)
        final_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        result = {"passed": True, "prepare_only": args.prepare, "source_commit": source_commit,
            "source_dirty": bool(initial_status), "source_stable": initial_status == final_status and source_commit == final_commit,
            "platform": "linux-x64", "matrix_rows": len(matrix), "policy_cases": len(policies),
            "local_closure_cases": closure_cases, "xml_negative_cases": xml_cases,
            "commands": len(commands), "artifact_hashes": hashes,
            "a13_tool": api_tool_inputs,
            "seconds": round(time.time()-started, 3), "scope": "Retained Core ABI/API compatibility; database package release identity and signature rejection are exercised by Packaging.Domain"}
        assert args.prepare or result["source_stable"]
        write(evidence / "result.json", result)
        print(json.dumps(result, indent=2))
    except BaseException as error:
        write(evidence / "failure.json", {"passed": False, "source_commit": source_commit, "error": repr(error), "commands": commands})
        raise


def xml_documentation_negatives(run, evidence, core_dll, core_config, api_tool, dotnet_root):
    """Synthesized delegate methods inherit docs; declared API remains mandatory."""
    delegate = "T:NativeWeave.Interop.NativeBufferReader`1"
    cases = (
        ("delegate-summary", delegate, "summary", "Missing XML summary: " + delegate),
        ("delegate-parameter", delegate, "param", "XML parameter names differ: " + delegate),
        ("delegate-return", delegate, "returns", "Missing XML returns: " + delegate),
        ("delegate-policy", delegate, "remarks", "Missing member or inherited type lifetime/cost remarks: " + delegate),
        ("declared-member-summary", "P:NativeWeave.Interop.EngineLoadOptions.NativeAssetPath", "summary",
         "Missing XML summary: P:NativeWeave.Interop.EngineLoadOptions.NativeAssetPath"),
    )
    config = compat.read_json(core_config)
    results = []
    for name, member_id, element_name, diagnostic in cases:
        directory = evidence / "xml-negative" / name
        directory.mkdir(parents=True)
        assembly = directory / core_dll.name
        shutil.copy2(core_dll, assembly)
        document = ET.parse(core_dll.with_suffix(".xml"))
        member = next(item for item in document.iter("member") if item.get("name") == member_id)
        element = member.find(element_name)
        assert element is not None, (name, member_id)
        member.remove(element)
        document.write(assembly.with_suffix(".xml"), encoding="utf-8", xml_declaration=True)
        altered = deepcopy(config)
        altered.update(assembly=str(assembly), output=str(directory / "reference.md"))
        candidate = write(directory / "config.json", altered)
        run("xml-negative-" + name, ["dotnet", api_tool, ROOT, dotnet_root, candidate, "--snapshot"],
            expected=None, contains=(diagnostic,))
        results.append({"case": name, "expected_rejection": diagnostic, "passed": True})
    write(evidence / "xml-negative-cases.json", results)
    return len(results)


def local_closure(run, evidence, common):
    """Real local NuGet restores, with deliberately unsigned graph-only packages."""
    feed = evidence / "local-feed"; feed.mkdir()
    config = evidence / "local-feed.config"
    config.write_text(f'<configuration><packageSources><clear/><add key="a22-fixtures" value="{feed}"/></packageSources><packageSourceMapping><clear/><packageSource key="a22-fixtures"><package pattern="NativeWeave.Compatibility.*"/></packageSource></packageSourceMapping></configuration>')
    versions = ("1.0.0", "1.1.0")
    closures, paths = {}, {}
    for version in versions:
        entries = []
        packages = {}
        for name in ("Native", "Facade"):
            identity = f"NativeWeave.Compatibility.{name}"
            dependencies = {"NativeWeave.Compatibility.Native": version} if name == "Facade" else {}
            deps = "".join(f'<dependency id="{key}" version="[{value}]" />' for key, value in dependencies.items())
            nuspec = f'<?xml version="1.0"?><package><metadata><id>{identity}</id><version>{version}</version><authors>Conformance</authors><description>Unpublished graph fixture</description><dependencies><group targetFramework="net11.0">{deps}</group></dependencies></metadata></package>'
            package = feed / f"{identity}.{version}.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr(identity + ".nuspec", nuspec)
                archive.writestr("README.md", "Unsigned restore-graph fixture only. Not a release payload.\n")
            entry = {"id": identity, "version": version, "dependencies": dependencies, "unsigned_entries": compat.package_entries(package)}
            entries.append(entry); packages[identity] = package
        closures[version] = {"schema_version": 1, "packages": entries}
        paths[version] = packages
    project = evidence / "closure-consumer"; project.mkdir()
    (project / "Program.cs").write_text('System.Console.WriteLine("Graph-only fixture");\n')
    cases = []
    def restore(name, version, override=None, expected=0):
        direct = f'<PackageReference Include="NativeWeave.Compatibility.Native" Version="[{override}]" />' if override else ""
        (project / "Consumer.csproj").write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType><RestorePackagesWithLockFile>false</RestorePackagesWithLockFile><NoWarn>NU1608</NoWarn></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Compatibility.Facade" Version="[{version}]" />{direct}</ItemGroup></Project>')
        run(f"closure-{name}-restore", ["dotnet", "restore", project / "Consumer.csproj", "--configfile", config,
            "--packages", project / ("cache-" + name), "--force", "-p:NuGetAudit=false", *common], expected=expected)
        assets = compat.read_json(project / "obj/project.assets.json")
        try:
            compat.validate_closure(closures[version], paths[version], assets)
        except compat.Rejected as error:
            assert override and str(error).startswith("NWPKG016:")
            cases.append({"name": name, "hard_rejection": str(error)})
        else:
            assert not override
            cases.append({"name": name, "complete": True})
    restore("old-complete", "1.0.0")
    restore("new-complete", "1.1.0")
    restore("direct-override-suppressed", "1.0.0", "1.1.0")
    restore("rollback-old-complete", "1.0.0")
    # Remove the exact native leaf from this local feed; a newer leaf must not be substituted.
    missing = paths["1.0.0"]["NativeWeave.Compatibility.Native"]
    held = missing.with_suffix(".held")
    missing.rename(held)
    try:
        (project / "Consumer.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Compatibility.Facade" Version="[1.0.0]" /></ItemGroup></Project>')
        run("closure-partial-set-restore", ["dotnet", "restore", project / "Consumer.csproj", "--configfile", config,
            "--packages", project / "empty-cache", "--force", "-p:NuGetAudit=false", *common], expected=None, contains=("NU1102",))
        cases.append({"name": "partial-set", "hard_rejection": "NU1102"})
    finally:
        held.rename(missing)
    # A bare minimum edge is not an exact owned dependency, independent of NuGet warnings.
    facade = paths["1.0.0"]["NativeWeave.Compatibility.Facade"]
    with zipfile.ZipFile(facade) as archive:
        name = "NativeWeave.Compatibility.Facade.nuspec"
        raw = archive.read(name).replace(b"[1.0.0]", b"1.0.0")
    bare = evidence / "bare-range.nupkg"
    with zipfile.ZipFile(facade) as original, zipfile.ZipFile(bare, "w") as candidate:
        for entry in original.infolist():
            candidate.writestr(entry, raw if entry.filename == name else original.read(entry))
    try:
        compat.package_payload(bare, closures["1.0.0"]["packages"][1])
        raise AssertionError("Bare owned range passed")
    except compat.Rejected as error:
        assert str(error).startswith("NWPKG012:")
        cases.append({"name": "bare-minimum-edge", "hard_rejection": str(error)})
    write(evidence / "local-closure-cases.json", {"scope": "Unsigned local graph fixtures; cryptographic acceptance is separate", "cases": cases})
    return len(cases)


if __name__ == "__main__":
    main()
