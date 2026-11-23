"""Check module declarations against CMake's configured File API target graph."""
import argparse
import json
import re
import subprocess
from pathlib import Path

from catalog import load_catalog


def query(build):
    directory = build / ".cmake/api/v1/query"
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "codemodel-v2").touch()


def verify(build, catalog, required=()):
    reply = build / ".cmake/api/v1/reply"
    index = json.loads(max(reply.glob("index-*.json")).read_text())
    model = json.loads((reply / index["reply"]["codemodel-v2"]["jsonFile"]).read_text())
    owners = catalog.targets
    source_dir = Path(model["paths"]["source"])
    for config in model["configurations"]:
        targets = {t["id"]: t for t in config["targets"]}
        present = {t["name"] for t in targets.values()}
        if set(required) - present:
            raise ValueError(f"Missing CMake targets: {set(required) - present}")
        # Compiler-recorded headers include indirect and conditional includes for this preset.
        headers = {}
        if (build / ".ninja_deps").exists():
            deps = subprocess.check_output(["ninja", "-C", str(build), "-t", "deps"], text=True)
            target = None
            for line in deps.splitlines():
                if line and not line[0].isspace():
                    match = re.search(r"(?:^|/)CMakeFiles/([^/]+)\.dir/", line)
                    target = match.group(1) if match else None
                elif target in owners and line.strip():
                    path = (build / line.strip()).resolve()
                    if path.is_relative_to(source_dir) and not path.is_relative_to(build.resolve()):
                        relative = path.relative_to(source_dir).as_posix()
                        if not relative.startswith("artifacts/"):
                            headers.setdefault(target, set()).add(relative)
        for t in targets.values():
            owner = owners.get(t["name"])
            if owner is None:
                continue
            detail = json.loads((reply / t["jsonFile"]).read_text())
            used = {catalog.owner(path)[0] for path in headers.get(t["name"], ())}
            for dep in detail.get("dependencies", []):
                other = owners.get(targets[dep["id"]]["name"])
                if other:
                    used.add(other)
            for source in detail.get("sources", []):
                path = source_dir / source["path"]
                if source.get("isGenerated") or not path.is_relative_to(source_dir):
                    continue
                relative = path.relative_to(source_dir).as_posix()
                if not relative.startswith("artifacts/"):
                    used.add(catalog.owner(relative)[0])
            missing = used - catalog.dependencies(owner)
            if missing:
                raise ValueError(f"{t['name']} uses unregistered module dependencies: {sorted(missing)}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["query", "verify"])
    parser.add_argument("build", type=Path)
    parser.add_argument("targets", nargs="*")
    args = parser.parse_args()
    if args.action == "query":
        query(args.build)
    else:
        verify(args.build, load_catalog(), args.targets)
