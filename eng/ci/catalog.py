"""Module ownership and reverse dependencies. Registrations use repository-relative paths."""

import fnmatch
import json
import posixpath
import re
import subprocess
import xml.etree.ElementTree as ET
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DIRECTORY = "eng/ci/modules"


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


class Catalog:
    def __init__(self, modules, *, historical=False):
        self.modules = modules
        self.rules = []
        self.closures = {}
        self.packages = {}
        self.targets = {}
        for name, spec in modules.items():
            if not re.fullmatch(r"[a-z][a-z0-9-]*", name):
                raise ValueError(f"Invalid module name: {name}")
            unknown = spec.keys() - {
                "sources",
                "metadata",
                "tests",
                "depends",
                "api_checks",
                "checks",
                "run",
                "package",
                "pack",
                "consume",
                "artifacts",
                "resources",
                "targets",
                "integration",
                "includes",
            }
            if unknown:
                raise ValueError(f"{name}: unknown registration fields {sorted(unknown)}")
            for field in (
                "sources",
                "metadata",
                "tests",
                "depends",
                "api_checks",
                "checks",
                "run",
                "pack",
                "consume",
                "resources",
                "targets",
                "integration",
                "includes",
            ):
                values = spec.get(field, [])
                if not isinstance(values, list) or not all(isinstance(v, str) and v for v in values):
                    raise ValueError(f"{name}.{field} must be an array of strings")
            resources = set(spec.get("resources", []))
            unsupported = resources - {
                "dotnet",
                "native",
                "pg-tools",
                "pg-server",
                "design",
                "scope-design",
                "plan",
                "qemu",
            }
            if unsupported:
                raise ValueError(f"{name}: unknown runner resources {sorted(unsupported)}")
            for target in spec.get("targets", []):
                if target in self.targets:
                    raise ValueError(f"Duplicate target {target} in {name}")
                self.targets[target] = name
            if "artifacts" in spec and (
                not isinstance(spec["artifacts"], str)
                or not re.fullmatch(r"artifacts/[a-zA-Z0-9_./-]+", spec["artifacts"])
                or ".." in Path(spec["artifacts"]).parts
                or not spec.get("run")
            ):
                raise ValueError(f"{name}: give the module's run command an output under artifacts/")
            if (
                "package" in spec
                and not (spec.get("pack") and spec.get("consume"))
                and not (name in {"sqlite", "postgresql"} and spec.get("checks"))
            ):
                raise ValueError(f"{name}: a top-level package requires pack and consume commands")
            for kind in ("sources", "metadata", "tests"):
                for pattern in spec.get(kind, []):
                    # An exact file overrides a directory, and a nested directory overrides its parent.
                    prefix = pattern.split("*", 1)[0]
                    self.rules.append((len(prefix), pattern, name, kind))
            if "package" in spec:
                identity = spec["package"]
                prefix = r"(?:NativeWeave|Korda)" if historical else "Korda"
                if not re.fullmatch(prefix + r"\.[A-Za-z0-9.-]+", identity) or identity.lower() in (
                    p.lower() for p in self.packages.values()
                ):
                    raise ValueError(f"Invalid or duplicate public package: {identity}")
                self.packages[name] = identity
        self.rules.sort(reverse=True)
        for name in modules:
            self.dependencies(name)

    def dependencies(self, name, visiting=()):
        if name in self.closures:
            return self.closures[name]
        if name not in self.modules or name in visiting:
            raise ValueError("Invalid module dependency: " + " -> ".join((*visiting, name)))
        result = {name}
        for dependency in self.modules[name].get("depends", []) + self.modules[name].get("includes", []):
            result.update(self.dependencies(dependency, (*visiting, name)))
        self.closures[name] = result
        return result

    @lru_cache(maxsize=None)
    def owner(self, path):
        best = None
        for specificity, pattern, name, kind in self.rules:
            if best and specificity < best[0]:
                break
            match = path.startswith(pattern) if pattern.endswith("/") else fnmatch.fnmatchcase(path, pattern)
            if match:
                if best and best[1:] != (name, kind):
                    raise ValueError(f"Ambiguous ownership for {path}: {best[1]} and {name}")
                best = (specificity, name, kind)
        if not best:
            raise ValueError(f"Unregistered CI path: {path}. Add its module under {DIRECTORY}/")
        return best[1:]

    def add_project_references(self, projects):
        # Read explicit MSBuild edges, including conditional ones (conservative union).
        # Relative source roots cover linked files and dynamically loaded native assets.
        for path, xml in projects:
            module, kind = self.owner(path)
            if kind != "sources":
                continue
            for node in ET.fromstring(xml).iter("ProjectReference"):
                include = node.get("Include", "").replace("\\", "/")
                if not include or "$" in include:
                    continue
                target = posixpath.normpath(posixpath.join(posixpath.dirname(path), include))
                other, _ = self.owner(target)
                deps = self.modules[module].setdefault("depends", [])
                if other != module and other not in deps:
                    deps.append(other)
        self.closures.clear()
        for name in self.modules:
            self.dependencies(name)

    def resources(self, name):
        resources = set(self.modules[name].get("resources", []))
        if "pg-server" in resources:
            resources.add("pg-tools")
        if "pg-tools" in resources:
            resources.add("native")
        return sorted(resources)

    def consumers(self, sources):
        return {name for name, closure in self.closures.items() if sources & closure}


def load_catalog(tree=None, *, historical=False):
    modules = {}
    if tree is None:
        documents = [(p.name, p.read_bytes()) for p in sorted((ROOT / DIRECTORY).glob("*.json"))]
    else:
        names = git("ls-tree", "-r", "--name-only", tree, "--", DIRECTORY).decode().splitlines()
        documents = [(p, git("show", f"{tree}:{p}")) for p in names if p.endswith(".json")]
    for path, document in documents:
        registrations = json.loads(document)
        if modules.keys() & registrations.keys():
            raise ValueError(f"Duplicate module in {path}")
        modules.update(registrations)
    catalog = Catalog(modules, historical=historical)
    if tree and modules:
        projects = git("ls-tree", "-r", "--name-only", tree).decode().splitlines()
        catalog.add_project_references(
            (p, git("show", tree + ":" + p))
            for p in projects
            if p.endswith(".csproj") and catalog.owner(p)[1] == "sources"
        )
    return catalog
