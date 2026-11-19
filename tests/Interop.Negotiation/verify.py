#!/usr/bin/env python3
"""Execute and preserve the scoped A02 Linux x64 component evidence."""
import argparse
import hashlib
import json
import os
import platform
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OWNED = ["src/Interop.Negotiation", "tests/Interop.Negotiation", "native/interop/negotiation",
         "samples/api/interop-negotiation", "docs/guides/interop-negotiation.md"]
COMMON = ["src/NativeWeave.Core", "src/Interop.Errors", "src/Interop.Abi", "native/interop/abi",
          "global.json", "rust-toolchain.toml", "Cargo.lock", "Directory.Build.props", "Directory.Build.targets"]
CASES = ["metadata", "coexist", "concurrent", "output-record", "options", "minor-old", "future-size",
         "bad-major", "bad-size", "bad-pointer", "bad-reserved", "bad-pending", "bad-unknown",
         "missing-export", "major-requirement", "minor-requirement", "missing-asset", "hash-mismatch",
         "invalid-binary", "wrong-rid", "cpu-profile", "conflict", "latched-failure", "preload-retry",
         "default-missing", "cached-override", "default-concurrent", "default-retry",
         "default-options", "default-conflict"]
VARIANTS = {"alpha": None, "beta": "fixture_beta", "minor-zero": "minor_zero", "future-size": "future_size",
            **{name: name.replace("-", "_") for name in CASES if name.startswith("bad-")}}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def source_files(scopes):
    result = {}
    for scope in scopes:
        path = ROOT / scope
        for item in sorted(path.rglob("*")) if path.is_dir() else [path]:
            if item.is_file() and not any(p in {"bin", "obj", "__pycache__"} for p in item.relative_to(ROOT).parts):
                result[str(item.relative_to(ROOT))] = sha(item)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan-root", type=Path, required=True)
    parser.add_argument("--design-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--nuget-config", type=Path)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    if sys.platform != "linux" or platform.machine() != "x86_64":
        raise ValueError("This runner records Linux x64 only.")
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    environment = os.environ.copy()
    environment.update(DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDUSESERVER="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise ValueError("The pinned dotnet SDK must be on PATH.")
    sdk_root = Path(dotnet).resolve().parent
    commands = []
    result = {"task": "A02", "platform": "linux-x64", "passed": False, "commands": commands,
              "native_aot_executed": False, "test_modules_only": True, "other_platforms_executed": False,
              "restore_mode": "offline fixed Microsoft SDK packages; audit refresh unavailable" if args.offline else "repository policy",
              "package_scope": "local verification envelopes; no public publication or release expansion"}

    def write(name, value):
        (out / name).write_text(json.dumps(value, indent=2) + "\n")

    def run(name, command, cwd=ROOT, env=environment, expected=0):
        command = [str(x) for x in command]
        started = time.monotonic()
        proc = subprocess.run(command, cwd=cwd, env=env, capture_output=True, timeout=300)
        (out / (name + ".stdout")).write_bytes(proc.stdout)
        (out / (name + ".stderr")).write_bytes(proc.stderr)
        commands.append({"name": name, "command": command, "cwd": str(cwd), "exit_code": proc.returncode,
                         "elapsed_seconds": round(time.monotonic() - started, 3),
                         "stdout_sha256": sha(out / (name + ".stdout")), "stderr_sha256": sha(out / (name + ".stderr"))})
        write("commands.json", commands)
        if proc.returncode != expected:
            raise RuntimeError(f"{name} returned {proc.returncode}: " + (proc.stderr + proc.stdout).decode(errors="replace")[-5000:])
        return proc.stdout.decode()

    build_options = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
    restore_options = ["--configfile", args.nuget_config.resolve()] if args.nuget_config else []
    if args.offline:
        restore_options += ["-p:NuGetAudit=false"]

    def restore(name, project, aot=False, cwd=ROOT, env=environment, config_options=restore_options, locked=True):
        run(name, [dotnet, "restore", project, *( ["--locked-mode"] if locked else []),
                   *( ["-r", "linux-x64", "-p:PublishAot=true"] if aot else []),
                   *config_options, *build_options], cwd, env)

    def build(name, project):
        run(name, [dotnet, "build", project, "-c", "Release", "--no-restore", *build_options])

    try:
        result["source_commit"] = run("source-commit", ["git", "rev-parse", "HEAD"]).strip()
        write("source-identity-before.json", {"commit": result["source_commit"], "owned": source_files(OWNED),
              "common_inputs": source_files(COMMON), "status": run("source-status", ["git", "status", "--short"])})
        baseline = json.loads((args.plan_root / "source-driven-baseline.json").read_text())
        actual_design = run("design-head", ["git", "-C", args.design_root, "rev-parse", "HEAD"]).strip()
        if actual_design != baseline["design_main"]:
            raise ValueError("The Design checkout differs from the Plan lock.")
        node = next(n for n in json.loads((args.plan_root / "delivery-graph.json").read_text())["nodes"] if n["id"] == "A02")
        checked = {}
        for key, expected in node["api_design_input_sha256"].items():
            relative = key.split(":", 1)[1]
            actual = sha(args.design_root / relative)
            if actual != expected:
                raise ValueError("Design API input mismatch: " + key)
            checked[key] = actual
        write("pinned-design-inputs.json", {"commit": actual_design, "task_revision": node["revision"], "sha256": checked})
        toolchain = {"sdk": run("sdk-version", [dotnet, "--version"]).strip(),
                     "dotnet_info": run("dotnet-info", [dotnet, "--info"]),
                     "rustc": run("rustc-version", ["rustc", "--version", "--verbose"]),
                     "platform": platform.platform(), "architecture": platform.machine()}
        if toolchain["sdk"] != "11.0.100" or not toolchain["rustc"].startswith("rustc 1.100.0 "):
            raise ValueError("Use the exact pinned SDK 11.0.100 / Rust 1.100.0.")

        fixtures = out / "fixtures"
        fixtures.mkdir()
        run("native-format", ["rustfmt", "--edition", "2024", "--check", "native/interop/negotiation/fixtures/bootstrap.rs"])
        rlib = fixtures / "libnativeweave_interop_abi.rlib"
        run("canonical-abi-build", ["rustc", "--edition=2024", "--crate-name", "nativeweave_interop_abi", "--crate-type", "rlib",
            "-Dwarnings", "-Dunsafe_op_in_unsafe_fn", "-C", "opt-level=2", "-C", "target-cpu=x86-64", "native/interop/abi/src/lib.rs", "-o", rlib])
        native_hashes = {}
        for variant, cfg in VARIANTS.items():
            library = fixtures / ("libnw_a02_" + variant.replace("-", "_") + ".so")
            output = run("native-" + variant, ["rustc", "--edition=2024", "--crate-type", "cdylib", "-Dwarnings", "-Dunsafe_op_in_unsafe_fn",
                "-C", "opt-level=2", "-C", "target-cpu=x86-64", "--extern", "nativeweave_interop_abi=" + str(rlib),
                "native/interop/negotiation/fixtures/bootstrap.rs", "-o", library, *( ["--cfg", cfg] if cfg else []),
                *( ["--print=link-args"] if variant == "alpha" else [])])
            if variant == "alpha":
                link = shlex.split(output)
                while link and "=" in link[0]: link.pop(0)
                driver = Path(shutil.which(link[0]) or link[0]).resolve()
                search = [x for x in link[1:] if x.startswith("-B")]
                flavor = next((x.split("=", 1)[1] for x in link if x.startswith("-fuse-ld=")), None)
                linker_name = run("rust-linker-selection", [driver, *search, "-print-prog-name=" + ("ld." + flavor if flavor else "ld")]).strip()
                linker = Path(shutil.which(linker_name) or linker_name).resolve()
                toolchain["rust_linker"] = {"driver": str(driver), "driver_sha256": sha(driver), "linker": str(linker),
                    "linker_sha256": sha(linker), "version": run("rust-linker-version", [linker, "--version"]), "actual_link_arguments": link}
            symbols = run("native-symbols-" + variant, ["nm", "-D", "--defined-only", library])
            actual = {line.split()[-1] for line in symbols.splitlines()}
            wanted = {"nw_a02_beta_get_abi_v1", "nw_a02_beta_probe"} if variant == "beta" else {"nw_a02_alpha_get_abi_v1", "nw_a02_alpha_probe", "nw_a02_alpha_bootstrap_calls"}
            if actual != wanted:
                raise ValueError("Native fixture export inventory differs: " + variant)
            native_hashes[variant] = sha(library)
        write("native-artifacts.json", native_hashes)
        run("native-dependencies", ["readelf", "-d", fixtures / "libnw_a02_alpha.so"])

        test_project = "tests/Interop.Negotiation/Interop.Negotiation.Tests.csproj"
        check_project = "tests/Interop.Negotiation/ApiCheck/ApiCheck.csproj"
        sample_project = "samples/api/interop-negotiation/Interop.Negotiation.Sample.csproj"
        restore("test-restore", test_project); build("test-build", test_project)
        restore("api-restore", check_project); build("api-build", check_project)
        run("api-xml-reference", [dotnet, "tests/Interop.Negotiation/ApiCheck/bin/Release/net11.0/ApiCheck.dll", ROOT, sdk_root,
                                   "src/NativeWeave.Core/bin/Release/net11.0/NativeWeave.Core.dll"])
        rows = []
        for case in CASES:
            rows.append(json.loads(run("jit-" + case, [dotnet, "tests/Interop.Negotiation/bin/Release/net11.0/NativeWeave.Interop.Negotiation.Tests.dll", case, fixtures])))
        restore("test-aot-restore", test_project, aot=True)
        aot = out / "aot-tests"
        run("test-aot-publish", [dotnet, "publish", test_project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "--no-restore", "-o", aot, *build_options])
        selection = json.loads(run("aot-linker-selection", [dotnet, "msbuild", test_project, "-nologo", "-t:ResolveReferences,SetupOSSpecificProps",
            "-p:Configuration=Release", "-p:RuntimeIdentifier=linux-x64", "-p:PublishAot=true", "-getProperty:CppCompilerAndLinker,CppLinker,LinkerFlavor", *build_options]))["Properties"]
        driver = Path(shutil.which(selection["CppLinker"] or selection["CppCompilerAndLinker"]) or selection["CppCompilerAndLinker"]).resolve()
        name = "ld." + selection["LinkerFlavor"] if selection["LinkerFlavor"] else "ld"
        linker_name = run("aot-driver-linker", [driver, "-print-prog-name=" + name]).strip()
        linker = Path(shutil.which(linker_name) or linker_name).resolve()
        toolchain["aot_linker"] = {"selection": selection, "driver": str(driver), "driver_sha256": sha(driver), "linker": str(linker),
            "linker_sha256": sha(linker), "version": run("aot-linker-version", [linker, "--version"])}
        write("toolchain.json", toolchain)
        run("managed-format", [dotnet, "format", "whitespace", ".", "--folder", "--include", "src/Interop.Negotiation",
            "tests/Interop.Negotiation", "samples/api/interop-negotiation", "--verify-no-changes", "--verbosity", "minimal"])
        run("aot-artifact-comment", ["readelf", "-p", ".comment", aot / "NativeWeave.Interop.Negotiation.Tests"])
        for case in CASES:
            row = json.loads(run("aot-" + case, [aot / "NativeWeave.Interop.Negotiation.Tests", case, fixtures, "--require-aot"]))
            if row["dynamicCodeSupported"]:
                raise ValueError("An AOT claim still supports dynamic code.")
            rows.append(row)
        write("case-results.json", rows)

        # Source-project samples use explicitly installed fixture outputs. Their
        # package-consumer counterpart below uses actual SDK selected locations.
        def install_sample(directory, locations=None):
            for suffix in ("alpha", "beta"):
                filename = "libnw_a02_" + suffix + ".so"
                relative = locations[suffix] if locations else "A02-assets/" + filename
                target = directory / relative
                if not locations:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(fixtures / filename, target)
                if not target.is_file() or sha(target) != native_hashes[suffix]:
                    raise ValueError("SDK output native bytes differ: " + suffix)
                record = {"schemaVersion": 1, "engineId": "sample." + suffix, "packageId": "NativeWeave.A02.Sample",
                          "packageVersion": "0.1.0-preview.1", "rid": "linux-x64",
                          "sourcePath": "runtimes/linux-x64/native/" + filename, "outputPath": relative, "sha256": native_hashes[suffix]}
                (directory / ("NativeWeave.sample." + suffix + ".assets.json")).write_text(json.dumps(record))
        hashes = [native_hashes["alpha"], native_hashes["beta"]]
        restore("sample-restore", sample_project); build("sample-build", sample_project)
        sample_bin = ROOT / "samples/api/interop-negotiation/bin/Release/net11.0"
        install_sample(sample_bin)
        run("sample-jit", [dotnet, sample_bin / "NativeWeave.Interop.Negotiation.Sample.dll", *hashes])

        # Test-only NuGet envelopes exercise the actual product binaries without
        # changing product release metadata or installing a consumer Rust hook.
        feed = out / "packages"; feed.mkdir()
        version = "0.1.0-preview.1"
        def package(package_id, files):
            node = ET.Element("package"); metadata = ET.SubElement(node, "metadata")
            for key, value in {"id": package_id, "version": version, "authors": "NativeWeave A02 verifier",
                               "description": "Local component-verification envelope; not a release package."}.items():
                ET.SubElement(metadata, key).text = value
            path = feed / (package_id + "." + version + ".nupkg")
            with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
                archive.writestr(package_id + ".nuspec", ET.tostring(node, encoding="utf-8", xml_declaration=True))
                for source, destination in files:
                    archive.write(source, destination)
            return path
        core_bin = ROOT / "src/NativeWeave.Core/bin/Release/net11.0"
        core_package = package("NativeWeave.Core.A02.Verification", [(core_bin / name, "lib/net11.0/" + name)
            for name in ("NativeWeave.Core.dll", "NativeWeave.Core.xml", "NativeWeave.Interop.Abi.dll")])
        asset_package = package("NativeWeave.A02.NativeFixtures", [(fixtures / ("libnw_a02_" + suffix + ".so"),
            "runtimes/linux-x64/native/libnw_a02_" + suffix + ".so") for suffix in ("alpha", "beta")])
        consumer = Path(tempfile.mkdtemp(prefix="nativeweave-a02-consumer-"))
        (consumer / "Program.cs").write_text((ROOT / "samples/api/interop-negotiation/Program.cs").read_text())
        (consumer / "global.json").write_text((ROOT / "global.json").read_text())
        (consumer / "Consumer.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks><IsAotCompatible>true</IsAotCompatible><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <NuGetLockFilePath Condition="'$(PublishAot)' == 'true'">packages.linux-x64.aot.lock.json</NuGetLockFilePath></PropertyGroup>
  <ItemGroup><PackageReference Include="NativeWeave.Core.A02.Verification" Version="0.1.0-preview.1" />
    <PackageReference Include="NativeWeave.A02.NativeFixtures" Version="0.1.0-preview.1" /></ItemGroup>
</Project>''')
        config = ET.Element("configuration"); sources = ET.SubElement(config, "packageSources"); ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="a02-fixtures", value=str(feed))
        existing_config = args.nuget_config or ROOT / "NuGet.Config"
        for item in ET.parse(existing_config).getroot().findall("./packageSources/add"):
            ET.SubElement(sources, "add", key="source-" + item.attrib["key"], value=item.attrib["value"])
        config_file = consumer / "NuGet.Config"; ET.ElementTree(config).write(config_file, encoding="utf-8", xml_declaration=True)
        trap = consumer / "rust-trap"; trap.mkdir(); trap_log = consumer / "rust-invocations.txt"
        for name in ("cargo", "rustc", "rustup"):
            script = trap / name
            script.write_text("#!/bin/sh\nprintf '%s\\n' " + shlex.quote(name) + " >> " + shlex.quote(str(trap_log)) + "\nexit 97\n")
            script.chmod(0o755)
        consumer_env = environment | {"PATH": str(trap) + os.pathsep + environment["PATH"], "NUGET_PACKAGES": str(consumer / "nuget-cache")}
        consumer_restore = ["--configfile", config_file, *( ["-p:NuGetAudit=false"] if args.offline else [])]
        restore("consumer-first-restore", "Consumer.csproj", cwd=consumer, env=consumer_env, config_options=consumer_restore, locked=False)
        restore("consumer-locked-restore", "Consumer.csproj", cwd=consumer, env=consumer_env, config_options=consumer_restore)
        run("consumer-build", [dotnet, "build", "Consumer.csproj", "-c", "Release", "--no-restore", *build_options], consumer, consumer_env)

        def sdk_locations(publish=False):
            query = [dotnet, "msbuild", "Consumer.csproj", "-nologo", "-p:Configuration=Release", *build_options]
            if publish:
                query += ["-p:RuntimeIdentifier=linux-x64", "-p:PublishAot=true", "-t:ComputeFilesToPublish", "-getItem:ResolvedFileToPublish"]
            else:
                query += ["-t:ResolveReferences", "-getItem:NativeCopyLocalItems,RuntimeTargetsCopyLocalItems"]
            data = json.loads(run("consumer-sdk-publish-items" if publish else "consumer-sdk-build-items", query, consumer, consumer_env))
            matches = {}
            for items in data["Items"].values():
                for item in items:
                    for suffix in ("alpha", "beta"):
                        if item["Identity"].endswith("libnw_a02_" + suffix + ".so"):
                            relative = item.get("RelativePath") if publish else item.get("DestinationSubPath")
                            if not relative:
                                raise ValueError("The SDK did not report the exact native destination.")
                            matches[suffix] = relative.replace("\\", "/")
            if set(matches) != {"alpha", "beta"}:
                raise ValueError("The SDK native item inventory is incomplete.")
            return matches
        consumer_bin = consumer / "bin/Release/net11.0"
        build_locations = sdk_locations()
        install_sample(consumer_bin, build_locations)
        run("consumer-jit", [dotnet, consumer_bin / "Consumer.dll", *hashes], consumer, consumer_env)
        restore("consumer-aot-first-restore", "Consumer.csproj", aot=True, cwd=consumer, env=consumer_env, config_options=consumer_restore, locked=False)
        restore("consumer-aot-locked-restore", "Consumer.csproj", aot=True, cwd=consumer, env=consumer_env, config_options=consumer_restore)
        consumer_aot = consumer / "publish"
        run("consumer-aot-publish", [dotnet, "publish", "Consumer.csproj", "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "--no-restore", "-o", consumer_aot, *build_options], consumer, consumer_env)
        publish_locations = sdk_locations(True)
        install_sample(consumer_aot, publish_locations)
        run("consumer-aot", [consumer_aot / "Consumer", *hashes, "--require-aot"], consumer, consumer_env)
        if trap_log.exists():
            raise ValueError("A consumer attempted to invoke a Rust tool.")
        write("consumer-receipt.json", {"directory": str(consumer), "package_sha256": {core_package.name: sha(core_package), asset_package.name: sha(asset_package)},
              "jit": "passed", "native_aot": "passed", "rust_invocations": 0, "isolated_package_cache": True, "pristine_machine": False,
              "sdk_build_locations": build_locations, "sdk_publish_locations": publish_locations,
              "consumer_executable_sha256": sha(consumer_aot / "Consumer"),
              "consumer_source_sha256": sha(consumer / "Program.cs"), "ordinary_lock_sha256": sha(consumer / "packages.lock.json"),
              "aot_lock_sha256": sha(consumer / "packages.linux-x64.aot.lock.json")})
        result.update(passed=True, native_aot_executed=True, jit_cases=len(CASES), aot_cases=len(CASES),
                      assertions_per_mode=sum(r["assertions"] for r in rows[:len(CASES)]), compiled_public_symbols=84,
                      native_artifact_sha256=native_hashes,
                      aot_test_executable_sha256=sha(aot / "NativeWeave.Interop.Negotiation.Tests"))
        print(json.dumps({k: v for k, v in result.items() if k != "commands"}, indent=2))
    finally:
        write("source-identity-after.json", {"owned": source_files(OWNED), "common_inputs": source_files(COMMON)})
        write("result.json", result)


if __name__ == "__main__":
    main()
