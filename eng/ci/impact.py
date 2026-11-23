"""Plan module checks and package candidates from two Git trees."""

import argparse
import json
import posixpath
import xml.etree.ElementTree as ET
from pathlib import Path

from catalog import DIRECTORY, git, load_catalog
from registry import public_heads

from facts import IDS as IDS, PROVIDERS, files, fingerprints, inputs as inputs
from language import language, purpose
from database_plan import plan_actions


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
    owners = set()
    for path in paths:
        graph = catalog if path in entries else previous
        module, kind = graph.owner(path)
        role = purpose(path, kind)
        reasons.setdefault(module, []).append(path)
        if role == "documentation":
            continue
        if module in catalog.modules:
            direct.add(module)
        if role == "sources":
            if module in catalog.modules:
                owners.add(module)
            consumers = graph.consumers({module})
            affected_modules.update(consumers & catalog.modules.keys())
            affected_packages.update(consumers & catalog.packages.keys())
    # A manifest is also an input to routing. Test a changed command, and account for
    # added/removed dependencies even if no source file moved in the same commit.
    if previous is not catalog:
        for name, spec in catalog.modules.items():
            old = previous.modules.get(name, {})
            if any(spec.get(k) != old.get(k) for k in ("run", "integration", "pack", "consume", "includes")):
                direct.add(name)
    # An aggregate source edit exercises its own included components. A test-only
    # edit, or an edit in one of its children, does not pull in its siblings.
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
        # Recompute impact when registered dependency edges or packaging recipes changed.
        old_hashes = fingerprints(previous, old_entries)
        touched.update(name for name in catalog.packages if old_hashes.get(name) != hashes[name])
    db = set(PROVIDERS) & catalog.packages.keys()
    published = {p: {"version": None, "inputs": None} for p in catalog.packages}
    if touched:
        lookup = touched | (db if db & touched else set())
        published.update(public_heads({p: catalog.packages[p] for p in sorted(lookup)}))
    pending = {p for p in touched if published[p].get("inputs") != hashes[p]}
    selected = set(catalog.modules) if full else direct | consumers
    result = plan_actions(
        catalog,
        reasons,
        direct,
        touched & db,
        pending & db,
        previous=previous,
        entries=entries,
        full=full,
    )
    run_steps = {}
    for name in selected:
        spec = catalog.modules[name]
        step = "run" if name in direct or full or "package" not in spec else "integration"
        if spec.get(step):
            run_steps[name] = step
    modules = sorted(run_steps)
    changed_languages = sorted({language(p) for p in paths})
    languages = set(changed_languages) & {"python", "csharp"}
    if full or ".github/workflows/security.yml" in paths:
        languages.update(("python", "csharp"))
    releases = [{"module": "", "suffix": ""}] if pending & db else []
    releases.extend({"module": p, "suffix": "-" + p} for p in sorted(pending - db))
    validate_packages = set(catalog.packages) if full else pending | (direct & catalog.packages.keys())
    module_packages = sorted(validate_packages - db)
    return dict(
        result,
        changed_languages=changed_languages,
        inputs=hashes,
        published=published,
        packages=sorted(pending & db),
        candidates=sorted(pending),
        releases=releases,
        modules=modules,
        run_steps=run_steps,
        languages=sorted(languages),
        artifacts={n: catalog.modules[n]["artifacts"] for n in modules if "artifacts" in catalog.modules[n]},
        module_packages=module_packages,
        reasons=reasons,
        module_resources={
            n: sorted(
                set(catalog.modules[n].get("resources", []))
                | (
                    {"native", "pg-tools"}
                    if "pg-server" in catalog.modules[n].get("resources", [])
                    else {"native"}
                    if "pg-tools" in catalog.modules[n].get("resources", [])
                    else set()
                )
            )
            for n in set(modules) | set(module_packages)
        },
        compose_from={
            p: sorted(
                n for n in catalog.dependencies(p) & set(modules) if catalog.modules[n].get("artifacts")
            )
            for p in module_packages
        },
    )


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
        args.output.parent.mkdir(exist_ok=True, parents=True)
        args.output.write_text(data + "\n")


if __name__ == "__main__":
    main()
