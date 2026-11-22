"""Plan the checks and public Linux providers for a Git tree."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

PROVIDERS = ("sqlite", "postgresql")
IDS = dict(sqlite="NativeWeave.Database.Sqlite", postgresql="NativeWeave.Database.PostgreSql")
DOCS = {"README.md", "AGENTS.md", "CONTRIBUTING.md", "SECURITY.md"}
PACKAGE_INPUTS = ("src/Packaging.Linux/packages.py", "src/Packaging.Release/buildTransitive/",
                  "src/Packaging.Release/BuildTasks/", "eng/ci/bundles.py", "eng/ci/provider.targets")
NATIVE_INPUTS = ("native/", "CMake", "eng/task07_native", "eng/postgres-", "eng/cpp_toolchain")
PROVIDER_PATHS = {
    "sqlite": ("src/Data.Sqlite/", "src/Data.NativeHost/Providers/Sqlite/", "tests/Data.Sqlite/",
               "samples/api/data-sqlite/", "native/cpp/tests/sqlite_fixture_tests.cpp", "native/cpp/tests/fixtures/sqlite.cpp"),
    "postgresql": ("src/Data.Postgres/", "src/Data.NativeHost/Providers/PostgreSql/", "tests/Data.Postgres/",
                   "samples/api/data-postgres/", "native/cpp/patches/libpq-", "native/cpp/tests/fixtures/postgres"),
}


def rule(path):
    """Return a validation lane and the distributions this file can change."""
    if path in DOCS or path.startswith(("docs/", ".github/ISSUE_TEMPLATE/")):
        return "docs", ()
    if path.startswith(PACKAGE_INPUTS) or path in ("LICENSE", "THIRD_PARTY_NOTICES.md"):
        return "pack", PROVIDERS
    for provider, prefixes in PROVIDER_PATHS.items():
        if path.startswith(prefixes):
            return provider, () if path.startswith(("tests/", "samples/", "native/cpp/tests/")) else (provider,)
    if path == "tests/Packaging.Linux/test_profile.py":
        return "elf-profile", ()
    if path.startswith("tests/Packaging.Release/ApiCheck/"):
        return "pack-api", ()
    if path.startswith("tests/Query.Generator/") and not path.endswith("/verify.py"):
        return "generator", ()
    if path.startswith("tests/Tooling/"):
        return "tooling", ()
    if path.startswith(("tests/", "samples/", "native/cpp/tests/", ".github/", "eng/ci/")):
        return "full", ()
    if path.startswith("eng/") and not path.startswith(NATIVE_INPUTS):
        return "full", PROVIDERS if path == "eng/build.py" else ()
    return "full", PROVIDERS


def inputs(tree):
    """Version-independent fingerprints also catch changes left unpublished by a failed run."""
    result = {p: hashlib.sha256() for p in PROVIDERS}
    raw = subprocess.check_output(["git", "ls-tree", "-rz", tree])
    for entry in raw.split(b"\0"):
        if entry:
            path = entry.split(b"\t", 1)[1].decode()
            for provider in rule(path)[1]:
                result[provider].update(entry + b"\0")
    return {p: digest.hexdigest() for p, digest in result.items()}


def plan(paths, fingerprints, published, deep=False, *, recover=True):
    lanes, changed = set(), set()
    reasons = {}
    for path in paths:
        lane, packages = rule(path)
        lanes.add(lane)
        changed.update(packages)
        reasons.setdefault(lane, []).append(path)
    pending = {p for p in PROVIDERS if published[p].get("inputs") != fingerprints[p]}
    # Test-only PRs never build somebody else's unpublished release. Main can recover it.
    if not recover and not changed:
        pending.clear()
    bootstrap = any(not published[p].get("inputs") for p in PROVIDERS)
    if deep or (pending and bootstrap) or pending - changed or "full" in lanes or set(PROVIDERS) <= lanes:
        scope = "full"
    elif "pack" in lanes and lanes.intersection(PROVIDERS):
        scope = "full"
    elif lanes.intersection(PROVIDERS):
        scope = next(iter(lanes.intersection(PROVIDERS)))
    elif "pack" in lanes:
        scope = "pack"
    else:
        scope = "light"
    checks = lanes.intersection({"generator", "pack-api", "elf-profile"})
    if scope == "full":
        checks.difference_update({"generator", "pack-api"})  # The existing regression suite runs both.
    if scope == "pack":
        checks.add("pack-metadata")
        if any(p.startswith("src/Packaging.Release/BuildTasks/") for p in paths):
            checks.add("pack-api")
    rebuild_task = any(p.startswith("src/Packaging.Release/BuildTasks/") for p in paths)
    return {"consume": bool(pending) or deep or scope == "pack", "rebuild_task": rebuild_task,
            "checks": sorted(checks), "scope": scope, "packages": sorted(pending), "inputs": fingerprints,
            "published": published, "reasons": reasons, "deep": deep}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base")
    parser.add_argument("--tree", default="HEAD")
    parser.add_argument("--full", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    from registry import public_heads
    diff = subprocess.check_output(["git", "diff", "--no-renames", "--name-only", "-z", args.base, args.tree])
    paths = [p.decode() for p in diff.split(b"\0") if p]
    result = plan(paths, inputs(args.tree), public_heads(), args.full, recover=False)
    data = json.dumps(result, indent=2, sort_keys=True)
    print(data)
    if args.output:
        args.output.write_text(data + "\n")


if __name__ == "__main__":
    main()
