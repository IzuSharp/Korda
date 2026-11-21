#!/usr/bin/env python3
"""Build version-labelled HTML from checked compiled assemblies/XML and guides.

No publication is performed. --snapshot writes a proposed API review artifact;
normal execution checks the reviewed baseline before DocFX metadata/build.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]


def execute(args, root, logs, label, env):
    result = subprocess.run([str(a) for a in args], cwd=root, env=env, capture_output=True)
    (logs / (label + ".stdout")).write_bytes(result.stdout)
    (logs / (label + ".stderr")).write_bytes(result.stderr)
    if result.returncode:
        for stream in (result.stdout, result.stderr):
            if stream:
                print(stream.decode(errors="replace")[-16000:], file=sys.stderr)
        raise RuntimeError(label + " failed: inspect retained documentation logs")
    return result.stdout.decode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--snapshot", action="store_true")
    args = parser.parse_args()
    root, output = args.root.resolve(), args.output.resolve()
    if not output.is_relative_to(root / "artifacts"):
        raise ValueError("Documentation output must stay in this worktree's artifacts directory")
    output.mkdir(parents=True, exist_ok=True)
    logs = output / "logs"
    logs.mkdir(exist_ok=True)
    config = json.loads(args.config.read_text())
    env = os.environ.copy()
    env["DOTNET_ROLL_FORWARD"] = "LatestMajor"
    tool_manifest = root / "src/Build.Integration/.config/dotnet-tools.json"
    execute(["dotnet", "tool", "restore", "--tool-manifest", tool_manifest], root, logs, "docfx-restore", env)
    tool_root = tool_manifest.parent.parent
    version = execute(["dotnet", "tool", "run", "docfx", "--", "--version"], tool_root, logs, "docfx-version", env).strip()
    if not version.startswith("2.81.0+"):
        raise RuntimeError("Unexpected DocFX version")
    sdk = Path(env["DOTNET_ROOT"])
    project = root / "src/Build.Integration/ApiReference/ApiReference.csproj"
    execute(["dotnet", "build", project, "-c", "Release", "--no-restore", "-p:UseSharedCompilation=false", "-m:1"], root, logs, "api-tool-build", env)
    check = ["dotnet", "run", "--project", project, "-c", "Release", "--no-build", "--", root, sdk, args.config.resolve()]
    if args.snapshot:
        check.append("--snapshot")
    else:
        check.append("--write")
    result = execute(check, root, logs, "compiled-api-and-xml", env)
    assembly = root / config["assembly"]
    xml = assembly.with_suffix(".xml")
    if not assembly.is_file() or not xml.is_file():
        raise RuntimeError("Documentation must consume built assembly and XML bytes")
    # Work only from a copy of the accepted inputs, never mutate source documentation.
    inputs = output / "inputs"
    if inputs.exists():
        shutil.rmtree(inputs)
    inputs.mkdir()
    for file in sorted(assembly.parent.glob("*.dll")):
        shutil.copy2(file, inputs / file.name)
    if config.get("compiler_host_references", False):
        # These reference-only files belong to the pinned compiler host. They
        # resolve the plug-in extension API for DocFX and never enter a package
        # or application runtime payload.
        host = project.parent / "bin/Release/net11.0"
        for name in ("Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"):
            shutil.copy2(host / name, inputs / name)
    shutil.copy2(xml, inputs / xml.name)
    checked_reference = root / config["output"]
    shutil.copy2(checked_reference, inputs / "reference.md")
    (inputs / "index.md").write_text("# " + config["title"] + "\n\n" + config["scope"] +
                                    "\n\n[Exact compiled API and XML](reference.md)\n", encoding="utf-8")
    toc = "- name: Component snapshot\n  href: index.md\n- name: Exact API\n  href: reference.md\n- name: API metadata\n  href: api/\n"
    guide_hashes = {}
    for index, guide in enumerate(config.get("guides", [])):
        source = (root / guide).resolve()
        if not source.is_relative_to(root) or not source.is_file():
            raise ValueError("A guide must be an existing source file inside the repository")
        name = "guide-" + str(index) + ".md"
        shutil.copy2(source, inputs / name)
        guide_hashes[source.relative_to(root).as_posix()] = hashlib.sha256((inputs / name).read_bytes()).hexdigest()
        toc += "- name: Guide " + str(index + 1) + "\n  href: " + name + "\n"
    if guide_hashes:
        # The retained database/Core guides share the migration annotation's
        # local link. Include that explicit dependency in every standalone
        # composition while retaining the existing guide-N.md layout.
        implementation = root / "docs/guides/cpp-native.md"
        shutil.copy2(implementation, inputs / "cpp-native.md")
        guide_hashes[implementation.relative_to(root).as_posix()] = hashlib.sha256(
            (inputs / "cpp-native.md").read_bytes()).hexdigest()
        toc += "- name: Native implementation\n  href: cpp-native.md\n"
    (inputs / "toc.yml").write_text(toc)
    docfx_config = {
        "metadata": [{"src": [{"src": str(inputs), "files": [assembly.name]}],
                      "dest": str(inputs / "api"), "disableGitFeatures": True,
                      "properties": {"TargetFramework": config.get("target_framework", "net11.0")}}],
        "build": {"content": [{"src": str(inputs), "files": ["**/*.md", "**/*.yml"]}],
                  "dest": str(output / "site"), "template": ["default", "modern"],
                  "globalMetadata": {"_appTitle": config["title"], "_disableContribution": True},
                  "disableGitFeatures": True, "warningsAsErrors": True}}
    configuration = output / "docfx.json"
    configuration.write_text(json.dumps(docfx_config, indent=2) + "\n")
    execute(["dotnet", "tool", "run", "docfx", "--", "metadata", configuration, "--warningsAsErrors"], tool_root, logs, "metadata", env)
    execute(["dotnet", "tool", "run", "docfx", "--", "build", configuration, "--warningsAsErrors"], tool_root, logs, "html", env)
    files = {p.relative_to(output / "site").as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted((output / "site").rglob("*")) if p.is_file()}
    if "index.html" not in files or not any(path.startswith("api/") and path.endswith(".html") for path in files):
        raise RuntimeError("DocFX did not generate the expected reference HTML")
    receipt = {"status": "passed", "docfx": version, "target_framework": config.get("target_framework", "net11.0"), "published": False,
               "snapshot_requires_review": args.snapshot, "compiled_api": json.loads(result.strip().splitlines()[-1]),
               "assembly_sha256": hashlib.sha256(assembly.read_bytes()).hexdigest(),
               "xml_sha256": hashlib.sha256(xml.read_bytes()).hexdigest(),
               "guide_sha256": guide_hashes,
               "reference_sha256": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(inputs.glob("*.dll"))},
               "site_sha256": files}
    (output / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps({"status": "passed", "site_files": len(files),
                      "html_files": sum(name.endswith(".html") for name in files),
                      "receipt": str(output / "result.json")}))


if __name__ == "__main__":
    main()
