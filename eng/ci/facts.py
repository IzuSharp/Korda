"""Read immutable Git and NuGet data for the managed planner and package verifier."""
import hashlib
import json
import sys
from catalog import git, load_catalog
from registry import public_heads

PROVIDERS = ("sqlite", "postgresql")
IDS = {p: name for p, name in load_catalog().packages.items() if p in PROVIDERS}


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
        for module in sorted(catalog.dependencies(name)):
            spec = catalog.modules[module]
            if spec.get("artifacts"):
                digest.update(json.dumps([module, spec["artifacts"], spec["run"]]).encode())
        if catalog.modules[name].get("pack"):
            digest.update(json.dumps([catalog.packages[name], catalog.modules[name]["pack"]]).encode())
    return {name: digest.hexdigest() for name, digest in digests.items()}


def inputs(tree):
    return fingerprints(load_catalog(tree), files(tree))


if __name__ == "__main__":
    command, *ids = sys.argv[1:]
    if command != "heads":
        raise ValueError(command)
    packages = load_catalog().packages
    print(json.dumps(public_heads({p: packages[p] for p in ids})))
