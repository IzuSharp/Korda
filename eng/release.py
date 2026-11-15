#!/usr/bin/env python3
"""Validate a retained Linux candidate, publish it, then verify registry consumption."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from build import ARTIFACTS, ROOT, consume, digest

INDEX_POLL_SECONDS = 120
INDEX_TIMEOUT_SECONDS = 20 * 60


def validate_version(version):
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9a-z-]+(?:\.[0-9a-z-]+)*)?", version):
        raise ValueError("Use a canonical lowercase major.minor.patch[-prerelease] version")
    if "-" in version and any(part.isdigit() and len(part) > 1 and part.startswith("0") for part in version.split("-", 1)[1].split(".")):
        raise ValueError("Numeric prerelease identifiers cannot have leading zeroes")
    return version


def select_version(env, requested=""):
    """Allocate once before building; later jobs use the retained identity output."""
    event, ref = env.get("GITHUB_EVENT_NAME"), env.get("GITHUB_REF", "")
    if not event:
        return validate_version(requested)
    if event == "push" and ref.startswith("refs/tags/v"):
        return validate_version(ref[len("refs/tags/v"):])
    if ref != "refs/heads/main" or event not in ("push", "workflow_dispatch"):
        raise ValueError("Release identity requires a merged main PR, main dispatch or version tag")
    if event == "push":
        commit, repository = env.get("GITHUB_SHA", ""), env.get("GITHUB_REPOSITORY", "")
        if not re.fullmatch(r"[0-9a-f]{40}", commit) or not repository:
            raise ValueError("Automatic previews require an exact source commit and repository")
        # gh api --paginate --slurp returns one array per page. Only the actual
        # merged PR commit qualifies; an associated open PR or head commit does not.
        pages = json.loads(Path(env["MERGE_CANDIDATES"]).read_text())
        if not any(pr.get("merged_at") and pr.get("merge_commit_sha") == commit
                   and pr.get("base", {}).get("ref") == "main"
                   and pr.get("base", {}).get("repo", {}).get("full_name") == repository
                   for page in pages for pr in page):
            return None
    if event == "workflow_dispatch" and requested:
        return validate_version(requested)
    prefix = ET.parse(ROOT / "Directory.Build.props").findtext(".//VersionPrefix", "")
    validate_version(prefix)
    if "-" in prefix:
        raise ValueError("VersionPrefix must contain only major.minor.patch")
    run, attempt = env.get("GITHUB_RUN_NUMBER", ""), env.get("GITHUB_RUN_ATTEMPT", "")
    if not all(re.fullmatch(r"[1-9][0-9]*", number) for number in (run, attempt)):
        raise ValueError("Automatic previews require a positive run number and attempt")
    return validate_version(f"{prefix}-preview.{run}.{attempt}")


def validate_candidate(version):
    directory = ARTIFACTS / "packages"
    package = directory / f"NativeWeave.HelloWorld.{version}.nupkg"
    expected_names = {package.name, "manifest.json"}
    checksums = {}
    for line in (directory / "SHA256SUMS").read_text().splitlines():
        checksum, name = line.split("  ", 1)
        if name not in expected_names or name in checksums or not re.fullmatch("[0-9a-f]{64}", checksum):
            raise ValueError("Unexpected, duplicate or malformed candidate checksum entry")
        checksums[name] = checksum
    if set(checksums) != expected_names:
        raise ValueError("Incomplete candidate checksum manifest")
    for name, expected in checksums.items():
        if digest(directory / name) != expected:
            raise ValueError(f"Candidate hash mismatch: {name}")
    metadata = json.loads((directory / "manifest.json").read_text())
    if metadata["packageId"] != "NativeWeave.HelloWorld" or metadata["packageVersion"] != version or metadata["rid"] != "linux-x64":
        raise ValueError("Wrong package identity or platform")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if metadata["sourceDirty"] or metadata["sourceCommit"] != head:
        raise ValueError("Release requires a clean candidate built from the publishing commit")
    with zipfile.ZipFile(package) as archive:
        if len(archive.namelist()) != len(set(archive.namelist())):
            raise ValueError("Duplicate archive entries")
        if json.loads(archive.read("nativeweave/manifest.json")) != metadata:
            raise ValueError("Embedded and external manifests differ")
        if hashlib.sha256(archive.read(metadata["nativePath"])).hexdigest() != metadata["nativeSha256"]:
            raise ValueError("Native payload hash mismatch")
    return package


def entries(package):
    with zipfile.ZipFile(package) as archive:
        if len(archive.namelist()) != len(set(archive.namelist())):
            raise ValueError("Duplicate registry archive entries")
        return {name: hashlib.sha256(archive.read(name)).hexdigest()
                for name in archive.namelist() if name != ".signature.p7s"}


def download_registry_package(version, downloaded):
    """Check immediately, then wait two minutes between not-yet-indexed responses."""
    url = f"https://api.nuget.org/v3-flatcontainer/nativeweave.helloworld/{version}/nativeweave.helloworld.{version}.nupkg"
    deadline = time.monotonic() + INDEX_TIMEOUT_SECONDS
    while True:
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                downloaded.write_bytes(response.read())
            return
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(
                    f"Package {version} was uploaded, but NuGet indexing was not observed "
                    "within 20 minutes. Check NuGet and publication.json before retrying; "
                    "the uploaded version cannot be overwritten."
                ) from error
            delay = min(INDEX_POLL_SECONDS, remaining)
            print(f"Waiting for NuGet indexing; next check in {delay:.0f} seconds "
                  f"({remaining:.0f} seconds remaining).", flush=True)
            time.sleep(delay)


def publish(version):
    package = validate_candidate(version)
    if not os.environ.get("NUGET_API_KEY"):
        raise ValueError("NuGet/login must supply NUGET_API_KEY")
    # .NET 11 reads the credential from the environment. Do not log it or put it in argv.
    subprocess.run(["dotnet", "nuget", "push", str(package), "--source", "https://api.nuget.org/v3/index.json"], check=True)
    evidence = ARTIFACTS / "publication.json"
    result = {"status": "uploaded-awaiting-verification", "version": version,
              "uploadSha256": digest(package)}
    evidence.write_text(json.dumps(result, indent=2) + "\n")
    registry = ARTIFACTS / "registry"
    registry.mkdir(exist_ok=True)
    downloaded = registry / package.name
    download_registry_package(version, downloaded)
    subprocess.run(["dotnet", "nuget", "verify", str(downloaded), "--all"], check=True)
    if entries(package) != entries(downloaded):
        raise ValueError("Registry payload differs from the tested candidate (excluding the verified signature only)")
    # Restore the exact registry bytes in an empty cache, then run both JIT and AOT.
    consume(version, registry)
    result.update({"status": "published-and-consumed", "registrySha256": digest(downloaded),
              "signatureVerified": True, "unsignedPayloadEntriesEqual": True,
              "registryPackageJitAndAotExecuted": True})
    evidence.write_text(json.dumps(result, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["identity", "validate", "publish"])
    parser.add_argument("--version", default=os.environ.get("RELEASE_VERSION", ""))
    args = parser.parse_args()
    if args.command == "identity":
        version = select_version(os.environ, args.version)
        eligible = version is not None
        output = os.environ.get("GITHUB_OUTPUT")
        if output:
            with open(output, "a", encoding="utf-8") as stream:
                stream.write(f"eligible={str(eligible).lower()}\n")
                if eligible:
                    stream.write(f"version={version}\n")
        message = (f"Allocated package version: `{version}`." if eligible else
                   "Skipped automatic publication: this main commit is not the merge commit of a merged PR.")
        print(message)
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as stream:
                stream.write(message + "\n")
    elif args.command == "validate":
        print(validate_candidate(validate_version(args.version)).name)
    else:
        publish(validate_version(args.version))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
