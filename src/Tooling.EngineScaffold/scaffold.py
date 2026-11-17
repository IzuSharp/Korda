#!/usr/bin/env python3
"""Generate an explicitly adopted Engine repository from reviewed adapter inputs.

This is a maintainer operation. It never runs during application restore/build,
chooses no domain algorithm, and invents no native lifetime implementation.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parents[2]
COMPONENTS = {"abi", "error", "ownership", "buffer", "callback", "async", "affinity"}
THREADING = {"Synchronous", "EngineLocalAsync", "SerializedWorker", "ExternalLoop"}


class ScaffoldError(ValueError):
    """A descriptor or reviewed source does not establish a safe Engine profile."""


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def fail(condition, message):
    if condition:
        raise ScaffoldError(message)


def relative(root, value):
    fail(not isinstance(value, str) or not value or "\\" in value or ":" in value,
         "Use canonical relative paths")
    parts = value.split("/")
    fail(PurePosixPath(value).is_absolute() or any(p in ("", ".", "..") for p in parts),
         "Absolute paths and traversal are not permitted")
    result = root / value
    fail(not result.resolve().is_relative_to(root.resolve()), "A source symlink escapes its root")
    return result


def checked_file(root, item):
    fail(not isinstance(item, dict) or set(item) != {"path", "sha256"}, "A source needs path and sha256")
    path = relative(root, item["path"])
    fail(not re.fullmatch(r"[0-9a-f]{64}", item["sha256"]), "Invalid source SHA-256")
    fail(not path.is_file() or digest(path) != item["sha256"], "Missing or changed source: " + item["path"])
    return path


def load(root, descriptor):
    data = json.loads(descriptor.read_text())
    keys = {"schema_version", "engine_id", "package", "abi", "threading", "components",
            "capabilities", "native", "binding", "upstream", "files", "dependencies", "coverage"}
    fail(set(data) != keys or data["schema_version"] != 1, "Unknown or incomplete scaffold schema")
    fail(not re.fullmatch(r"[a-z][a-z0-9_.-]{0,127}", data["engine_id"]), "Invalid Engine identity")
    package = data["package"]
    fail(set(package) != {"id", "version", "namespace", "assembly"}, "Incomplete package identity")
    for key in ("id", "namespace", "assembly"):
        fail(not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]{0,127}", package[key]), "Invalid package identifier")
    fail(not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", package["version"]),
         "Use an exact normalized package version")
    components = data["components"]
    fail(not isinstance(components, list) or components != sorted(set(components)), "Components must be sorted and unique")
    fail(not {"abi", "error"}.issubset(components) or not set(components).issubset(COMPONENTS),
         "Unknown required Core component or missing ABI/error boundary")
    fail(data["threading"] not in THREADING, "Unknown threading capability")
    fail("buffer" in components and "ownership" not in components, "Owned buffers require ownership")
    fail("async" in components and not {"callback", "ownership"}.issubset(components),
         "Async requires explicit callback and owner retirement")
    fail(data["threading"] == "Synchronous" and bool({"callback", "async", "affinity"}.intersection(components)),
         "A synchronous profile must not start a callback/executor/affinity runtime")
    fail(data["threading"] == "EngineLocalAsync" and "async" not in components, "Async runtime requires the async profile")
    fail(data["threading"] in {"SerializedWorker", "ExternalLoop"} and "affinity" not in components,
         "An affine execution profile must declare affinity")
    abi = data["abi"]
    fail(set(abi) != {"major", "minor", "bootstrap", "error_domain"}, "Incomplete ABI profile")
    fail(type(abi["major"]) is not int or not 1 <= abi["major"] <= 65535 or
         type(abi["minor"]) is not int or not 0 <= abi["minor"] <= 65535, "Invalid ABI version")
    fail(type(abi["error_domain"]) is not int or not 0 <= abi["error_domain"] <= 0xffffffff, "Invalid error domain")
    fail(not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,127}", abi["bootstrap"]), "Invalid bootstrap symbol")
    caps = data["capabilities"]
    fail(not isinstance(caps, list) or not 1 <= len(caps) <= 64, "Declare 1-64 exact capabilities")
    for cap in caps:
        fail(set(cap) != {"name", "bit"} or not re.fullmatch(r"[a-z][a-z0-9]*(?:\.[a-z0-9]+)+", cap["name"])
             or type(cap["bit"]) is not int or not 0 <= cap["bit"] < 64, "Invalid capability declaration")
    fail(len({c["name"] for c in caps}) != len(caps) or len({c["bit"] for c in caps}) != len(caps), "Duplicate capability")
    native = data["native"]
    fail(set(native) != {"asset", "filename", "rid", "target", "cpu", "link_mode"}, "Incomplete native asset")
    fail((native["rid"], native["target"], native["cpu"], native["link_mode"]) !=
         ("linux-x64", "x86_64-unknown-linux-gnu", "x64-v2", "dynamic"),
         "This scaffold is validated only for Linux x64-v2 GNU dynamic assets")
    fail(not re.fullmatch(r"lib[A-Za-z0-9_]+\.so", native["filename"]), "Invalid Linux native filename")
    checked_file(root, native["asset"])
    binding = data["binding"]
    fail(set(binding) != {"sidecar", "inventory", "adapter", "linked_symbols", "library"}, "Incomplete A09 binding inputs")
    fail(not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", binding["library"]), "Invalid static import library")
    for name in ("sidecar", "inventory", "adapter", "linked_symbols"):
        checked_file(root, binding[name])
    sidecar = json.loads(checked_file(root, binding["sidecar"]).read_text())
    fail(sidecar.get("schema_version") != 1, "Unsupported A09 binding schema")
    fail(sidecar["module"]["id"] != data["engine_id"] or sidecar["module"]["abi_major"] != abi["major"]
         or sidecar["module"]["abi_minor"] != abi["minor"], "Engine and binding identities differ")
    fail(set(sidecar["capabilities"]) != {c["name"] for c in caps}, "Capability and binding inventories differ")
    symbols = {e["symbol"] for e in sidecar["exports"]}
    fail(abi["bootstrap"] not in symbols, "The bootstrap is absent from the static export inventory")
    for owner in sidecar["owners"]:
        fail(owner["release"] not in symbols, "A native owner has no creator release")
    fail(bool(sidecar["owners"]) and "ownership" not in components, "Owners require the ownership component")
    fail(bool(sidecar["operations"]) != ("async" in components), "Async operations and opted-in components differ")
    fail(not data["upstream"], "Exact upstream provenance is required")
    for source in data["upstream"]:
        fail(set(source) != {"repository", "commit", "crate", "version", "checksum", "features", "patches", "license"},
             "Incomplete upstream/crate/features/patch/license provenance")
        fail(not source["repository"].startswith("https://") or not re.fullmatch(r"[0-9a-f]{40}", source["commit"])
             or not re.fullmatch(r"[0-9a-f]{64}", source["checksum"]) or not source["license"], "Invalid upstream identity")
        fail(source["features"] != sorted(set(source["features"])) or not isinstance(source["patches"], list),
             "Features/patches must be explicit")
        for patch in source["patches"]:
            checked_file(root, patch)
    fail(not isinstance(data["files"], dict) or not data["files"], "Supply actual reviewed facade/sample source")
    for destination, source in data["files"].items():
        relative(root, destination)
        fail(not destination.startswith(("src/Engine/", "samples/Consumer/", "docs/", "buildTransitive/")), "Unowned scaffold destination")
        fail(destination.endswith((".csproj", ".props")) or "ScaffoldPackage.g.cs" in destination,
             "Generated build/manifest files cannot be replaced by source inputs")
        checked_file(root, source)
    fail(not any(x.startswith("src/Engine/") and x.endswith(".cs") for x in data["files"]), "Supply a typed facade implementation")
    fail("samples/Consumer/Program.cs" not in data["files"], "Supply an actual consumer")
    fail(not {"docs/public-api.expected.json", "docs/guide.md"}.issubset(data["files"]),
         "Supply the reviewed compiled API baseline and actual usage guide")
    fail("NativeWeave.Core" not in data["dependencies"], "The explicit descriptor seam requires Core")
    for name, version in data["dependencies"].items():
        fail(not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*", name) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version),
             "Dependencies require exact package IDs and versions")
    checked_file(root, data["coverage"])
    return data


def run(command, cwd, log):
    result = subprocess.run([str(x) for x in command], cwd=cwd, capture_output=True, timeout=300)
    log.with_suffix(".stdout").write_bytes(result.stdout)
    log.with_suffix(".stderr").write_bytes(result.stderr)
    fail(result.returncode != 0, "Binding validation failed; inspect " + log.name)


def generate(root, descriptor, output):
    root, descriptor, output = root.resolve(), descriptor.resolve(), output.resolve()
    data = load(root, descriptor)
    fail(output.exists(), "Output already exists; generation never overwrites a repository")
    adoption = json.loads((root / "public-api-standard.json").read_text())
    standard = relative(root, adoption["localPath"])
    fail(digest(standard) != adoption["sha256"], "The adopted API standard changed")
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".nativeweave-scaffold-", dir=output.parent))
    try:
        logs = staging / "artifacts/generation"
        logs.mkdir(parents=True)
        b = data["binding"]
        inputs = [checked_file(root, b[name]) for name in ("sidecar", "inventory", "adapter", "linked_symbols")]
        generated = staging / "bindings"
        for language, arguments in (("CSharp", inputs + [b["library"], generated, data["package"]["namespace"] + ".Generated"]),
                                    ("Rust", inputs + [generated])):
            project = root / ("src/Generators." + language + "/NativeWeave.Generators." + language + ".csproj")
            run(["dotnet", "run", "--project", project, "-c", "Release", "--no-restore", "--", *arguments],
                root, logs / language.lower())
        for destination, item in data["files"].items():
            file = relative(staging, destination)
            file.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(checked_file(root, item), file)
        for path in ("global.json", "rust-toolchain.toml", "LICENSE"):
            shutil.copyfile(root / path, staging / path)
        standard_target = relative(staging, adoption["localPath"])
        standard_target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(standard, standard_target)
        adoption["scope"] = "This generated Engine explicitly adopts the exact standard. Product/platform acceptance requires actual conformance."
        (staging / "public-api-standard.json").write_text(json.dumps(adoption, indent=2) + "\n")
        templates = Path(__file__).parent / "templates"
        for path in ("AGENTS.md", "eng/verify.py", ".github/workflows/conformance.yml"):
            target = staging / path
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(templates / path, target)
        for name, item in b.items():
            if isinstance(item, dict):
                target = staging / "provenance" / (name + Path(item["path"]).suffix)
                target.parent.mkdir(exist_ok=True)
                shutil.copyfile(checked_file(root, item), target)
        shutil.copyfile(checked_file(root, data["coverage"]), staging / "provenance/source-coverage.json")
        native = data["native"]
        asset_path = "runtimes/" + native["rid"] + "/native/" + native["filename"]
        asset = staging / asset_path
        asset.parent.mkdir(parents=True)
        shutil.copyfile(checked_file(root, native["asset"]), asset)
        p, a = data["package"], data["abi"]
        capabilities = ",\n            ".join('new(new EngineCapability(' + json.dumps(c["name"]) + '), ' + str(c["bit"]) + ')' for c in data["capabilities"])
        source = '''// Maintainer-generated immutable metadata; this creates no Engine instance.
using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;
namespace %s;
internal static class ScaffoldPackage
{
    internal static EngineDescriptor Descriptor { get; } = EnginePackage.CreateDescriptor(
        new EnginePackageManifest(%s, %s, %s,
            new AbiVersion(%d, %d), %s, EngineThreadingModel.%s,
            [new NativeAssetDescriptor("linux-x64", %s, %s, "x64-v2")],
            [%s]), typeof(ScaffoldPackage).Assembly);
}
''' % (p["namespace"], json.dumps(data["engine_id"]), json.dumps(p["id"]), json.dumps(p["version"]),
       a["major"], a["minor"], json.dumps(a["bootstrap"]), data["threading"], json.dumps(asset_path),
       json.dumps(native["asset"]["sha256"]), capabilities)
        (staging / "src/Engine/ScaffoldPackage.g.cs").write_text(source)
        references = "\n".join('    <PackageReference Include="' + escape(k) + '" Version="[' + escape(v) + ']" />' for k, v in sorted(data["dependencies"].items()))
        project = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>%s</AssemblyName><PackageId>%s</PackageId><Version>%s</Version>
    <IsPackable>true</IsPackable><AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <GenerateDocumentationFile>true</GenerateDocumentationFile><IsAotCompatible>true</IsAotCompatible>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
  <ItemGroup>
%s
    <Compile Include="../../bindings/Bindings.g.cs" />
    <None Include="../../%s" Pack="true" PackagePath="%s" CopyToOutputDirectory="PreserveNewest" Link="%s" />
    <None Include="../../buildTransitive/**/*" Pack="true" PackagePath="buildTransitive/%%(RecursiveDir)%%(Filename)%%(Extension)" />
    <InternalsVisibleTo Include="%s.Conformance" />
  </ItemGroup>
</Project>
''' % (p["assembly"], p["id"], p["version"], references, asset_path, asset_path, native["filename"], p["assembly"])
        (staging / "src/Engine/Engine.csproj").write_text(project)
        (staging / "Directory.Build.props").write_text('''<Project><PropertyGroup>
<TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion><Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<Deterministic>true</Deterministic><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<IsPackable>false</IsPackable><UseSharedCompilation>false</UseSharedCompilation>
</PropertyGroup></Project>
''')
        (staging / "samples/Consumer/Consumer.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><AssemblyName>%s.Conformance</AssemblyName><AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup><ItemGroup><PackageReference Include="%s" Version="[%s]" /></ItemGroup></Project>
''' % (p["assembly"], p["id"], p["version"]))
        (staging / "samples/Consumer/AotGuard.cs").write_text('''using System.Runtime.CompilerServices;
internal static class AotGuard
{
    [ModuleInitializer]
    internal static void Check()
    {
        if (Environment.GetEnvironmentVariable("NATIVEWEAVE_REQUIRE_AOT") == "1" && RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("Actual Native AOT execution is required.");
    }
}
''')
        (staging / "api-reference.json").write_text(json.dumps({
            "assembly": "src/Engine/bin/Release/net11.0/" + p["assembly"] + ".dll",
            "sources": ["src/Engine"], "baselines": ["docs/public-api.expected.json"],
            "title": p["id"] + " API", "scope": "Compiled Engine API and XML; independent platform acceptance recorded separately.",
            "output": "artifacts/docs/api-reference.md", "guides": ["docs/guide.md"]}, indent=2) + "\n")
        (staging / "engine.json").write_text(json.dumps(data, sort_keys=True, indent=2) + "\n")
        (staging / ".gitignore").write_text("artifacts/\n**/bin/\n**/obj/\n")
        (staging / "README.md").write_text("# " + p["id"] + "\n\nMaintainer-generated Engine source and precompiled Linux asset.\n\n"
            "Run `python3 eng/verify.py --feed /absolute/reviewed/local-feed --sdk-root /absolute/pinned/NativeWeave` to check source hashes, "
            "compile/package the actual facade and execute its independent JIT and Native AOT consumer. "
            "The initial generated tree is not a release acceptance record. Ordinary applications restore "
            "precompiled packages; these maintainer checks are never imported into application builds.\n")
        sdk_files = ["src/Build.Integration/compose_docs.py", "src/Build.Integration/ApiReference/Program.cs",
                     "src/Build.Integration/ApiReference/ApiReference.csproj"]
        manifest = {"schema_version": 1, "descriptor_sha256": digest(descriptor), "template_sha256": {},
                    "sdk_tool_sha256": {name: digest(root / name) for name in sdk_files},
                    "files": {}, "accepted": False, "components": data["components"], "core_source_changed": False}
        for file in sorted(templates.rglob("*")):
            if file.is_file(): manifest["template_sha256"][file.relative_to(templates).as_posix()] = digest(file)
        for file in sorted(staging.rglob("*")):
            if file.is_file() and not file.is_relative_to(staging / "artifacts"):
                manifest["files"][file.relative_to(staging).as_posix()] = digest(file)
        (staging / "scaffold-manifest.json").write_text(json.dumps(manifest, sort_keys=True, indent=2) + "\n")
        staging.rename(output)
    except BaseException:
        shutil.rmtree(staging)
        raise
    return {"output": str(output), "engine_id": data["engine_id"], "accepted": False,
            "manifest_sha256": digest(output / "scaffold-manifest.json")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("descriptor", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--root", type=Path, default=ROOT)
    args = parser.parse_args()
    print(json.dumps(generate(args.root, args.descriptor, args.output)))


if __name__ == "__main__":
    main()
