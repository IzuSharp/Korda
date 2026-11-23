"""Plan module checks and package candidates from two Git trees."""
import argparse
import hashlib
import json
import posixpath
import xml.etree.ElementTree as ET
from pathlib import Path

from catalog import DIRECTORY, git, load_catalog
from database import PROVIDERS, build_plan
from registry import public_heads

# Used by the database packaging scripts, which consume these two providers together.
IDS = {name: identity for name, identity in load_catalog().packages.items() if name in PROVIDERS}


def files(tree):
    return {entry.split(b"\t", 1)[1].decode(): entry for entry in git("ls-tree", "-rz", tree).split(b"\0") if entry}


def fingerprints(catalog, entries):
    digests = {p: hashlib.sha256() for p in catalog.packages}
    for path, entry in entries.items():
        module, kind = catalog.owner(path)
        if kind == "sources":
            for name, digest in digests.items():
                if module in catalog.dependencies(name):
                    digest.update(entry + b"\0")
    for name, digest in digests.items():
        if catalog.modules[name].get("pack"):
            digest.update(json.dumps(catalog.modules[name]["pack"]).encode())
    return {name: digest.hexdigest() for name, digest in digests.items()}


def inputs(tree):
    return fingerprints(load_catalog(tree), files(tree))


def changed_paths(base, tree):
    diff = git("diff", "--no-renames", "--name-only", "-z", base, tree)
    return [p.decode() for p in diff.split(b"\0") if p]


def solution_projects(path, entry):
    """Separate project membership from solution-wide build settings."""
    if entry is None:
        return {}, ""
    blob = entry.split(b"\t", 1)[0].split()[-1].decode()
    root = ET.fromstring(git("cat-file", "blob", blob))
    projects = {}
    for parent in root.iter():
        for project in list(parent.findall("Project")):
            name = posixpath.normpath(posixpath.join(posixpath.dirname(path), project.attrib["Path"]))
            projects[name] = ET.tostring(project, encoding="unicode").strip()
            parent.remove(project)
    for folder in list(root.findall("Folder")):
        if not len(folder):
            root.remove(folder)
    return projects, ET.canonicalize(ET.tostring(root, encoding="unicode"), strip_text=True)


def classify(paths, catalog, entries, previous):
    direct, affected_packages, affected_modules, reasons = set(), set(), set(), {}
    for path in paths:
        graph = catalog if path in entries else previous
        module, kind = graph.owner(path)
        reasons.setdefault(module, []).append(path)
        if module in catalog.modules:
            direct.add(module)
        if kind == "sources":
            consumers = graph.consumers({module})
            affected_modules.update(consumers & catalog.modules.keys())
            affected_packages.update(consumers & catalog.packages.keys())
    # A manifest is also an input to routing. Test a changed command, and account for
    # added/removed dependencies even if no source file moved in the same commit.
    if previous is not catalog:
        for name, spec in catalog.modules.items():
            old = previous.modules.get(name, {})
            if spec != old:
                direct.add(name)
                if any(spec.get(k) != old.get(k) for k in ("sources", "depends", "includes", "package", "pack")):
                    consumers = catalog.consumers({name})
                    affected_modules.update(consumers)
                    affected_packages.update(consumers & catalog.packages.keys())
    # An aggregate source edit exercises its own included components. A test-only
    # edit, or an edit in one of its children, does not pull in its siblings.
    owners = {catalog.owner(p)[0] for p in paths if p in entries and catalog.owner(p)[1] == "sources"}
    for owner in owners:
        for child in catalog.modules[owner].get("includes", []):
            affected_modules.update(catalog.dependencies(child))
    return direct, affected_modules, affected_packages, reasons


def make_plan(catalog, paths, entries, previous, full=False, old_entries=None):
    direct, consumers, touched, reasons = classify(paths, catalog, entries, previous)
    if old_entries is not None:
        # The solution is an index shared by otherwise independent products.
        for path in (p for p in paths if p.endswith(".slnx")):
            old, old_settings = solution_projects(path, old_entries.get(path))
            new, settings = solution_projects(path, entries.get(path))
            for project in old.keys() | new.keys():
                if settings != old_settings or old.get(project) != new.get(project):
                    graph = catalog if project in entries else previous
                    owner, _ = graph.owner(project)
                    if owner in catalog.modules:
                        direct.add(owner)
    hashes = fingerprints(catalog, entries)
    if old_entries is not None and previous is not catalog:
        old_hashes = fingerprints(previous, old_entries)
        touched.update(p for p, digest in hashes.items() if old_hashes.get(p) != digest)
    db = set(PROVIDERS) & catalog.packages.keys()
    lookup = set(catalog.packages) if full else set(touched)
    db_checks = {s.removeprefix("db:") for name in (catalog.modules if full else direct)
                 for s in catalog.modules[name].get("checks", [])}
    if lookup & db or "public" in db_checks:
        lookup.update(db)  # The combined consumer also needs the peer's published version.
    published = {p: {} for p in catalog.packages}
    if lookup:
        published.update(public_heads({p: catalog.packages[p] for p in sorted(lookup)}))
    eligible = set(touched)
    if touched & db:
        eligible.update(db)  # Recover an outstanding release only inside this package family.
    pending = {p for p in eligible if published[p].get("inputs") != hashes[p]}
    selected = set(catalog.modules) if full else direct | consumers
    result = build_plan(db_checks, touched & db, pending & db, published, full)
    run_steps = {}
    for name in selected:
        spec = catalog.modules[name]
        step = "run" if name in direct or full or "package" not in spec else "integration"
        if spec.get(step):
            run_steps[name] = step
    modules = sorted(run_steps)
    languages = set()
    for path in paths:
        if path.endswith((".cs", ".csproj", ".slnx", ".props", ".targets", ".editorconfig", ".lock.json")) or path in ("global.json", "NuGet.Config"):
            languages.add("csharp")
        if path.endswith((".py", ".pyi", "requirements.txt", "pyproject.toml")):
            languages.add("python")
    if full or ".github/workflows/security.yml" in paths:
        languages.update(("csharp", "python"))
    releases = ([{"module": "", "suffix": ""}] if pending & db else [])
    releases.extend({"module": p, "suffix": "-" + p} for p in sorted(pending - db))
    module_packages = sorted((set(catalog.packages) if full else pending) - db)
    return dict(result, inputs=hashes, published=published, packages=sorted(pending & db),
                candidates=sorted(pending), releases=releases, modules=modules, run_steps=run_steps, languages=sorted(languages),
                artifacts={n: catalog.modules[n]["artifacts"] for n in modules if "artifacts" in catalog.modules[n]},
                module_packages=module_packages, reasons=reasons,
                compose_from={p: sorted(n for n in catalog.dependencies(p) & set(modules)
                                        if catalog.modules[n].get("artifacts")) for p in module_packages})


def plan_tree(base, tree="HEAD", *, replay=None, full=False):
    paths = changed_paths(base, tree) if replay is None else replay
    catalog = load_catalog(tree)
    previous = catalog
    old_files = files(base)
    has_registry = any(p.startswith(DIRECTORY + "/") for p in old_files)
    if has_registry and any(p.startswith(DIRECTORY + "/") or p.endswith(".csproj") for p in paths):
        previous = load_catalog(base)
    entries = files(tree)
    if replay is not None:
        for p in replay:
            if p not in entries or catalog.owner(p)[1] != "tests":
                raise ValueError("Replay accepts tracked test or documentation paths only: " + p)
    return make_plan(catalog, paths, entries, previous, full, old_files)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base")
    parser.add_argument("--tree", default="HEAD")
    parser.add_argument("--full", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    data = json.dumps(plan_tree(args.base, args.tree, full=args.full), indent=2, sort_keys=True)
    print(data)
    if args.output:
        args.output.write_text(data + "\n")


if __name__ == "__main__":
    main()
