#!/usr/bin/env python3
"""Promote the accepted DB-CPP-01 archives to the owner's NuGet.org account.

This is a registry adapter, not a package builder. The original local-only
candidate records remain unchanged. Public publication and HelloWorld unlisting
have separate, incremental receipts; neither is inferred from an HTTP push alone.

Protocol: https://learn.microsoft.com/nuget/api/package-publish-resource
Signing: https://learn.microsoft.com/nuget/api/repository-signatures-resource
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import gzip
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import build_opener, HTTPRedirectHandler, Request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
INDEX = "https://api.nuget.org/v3/index.json"
PUBLISH = "https://www.nuget.org/api/v2/package"
GITHUB = "https://api.github.com/repos/IzuSharp/NativeWeave"
OWNER = "dekueon"
RETIRED = "NativeWeave.HelloWorld"
PINNED = {
    "repository": "IzuSharp/NativeWeave",
    "sourceCommit": "3badc05be503991b5591732629dd7e5b0dbad111",
    "runId": 44977773509,
    "artifactId": 14738115742,
    "artifactName": "nativeweave-database-linux-44977773509-1",
    "artifactSha256": "bb63f9fe71ea4c2f68dd63b748af3820c8bc0174199ce6fa7e2a92197f31e8b9",
    "artifactSize": 6169499,
    "candidatePath": "db-scope/prepared/candidate",
    "packageOwner": OWNER,
    "retiredPackageId": RETIRED,
}
MAX_JSON = 16 * 1024 * 1024
MAX_PACKAGE = 32 * 1024 * 1024
WAIT_SECONDS = 1200
POLL_SECONDS = 15


def module(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


compat = module("nuget_publication_compat", "src/Verification.Compatibility/compatibility.py")
domain = module("nuget_publication_domain", "src/Packaging.Domain/model.py")
release = module("nuget_publication_release", "src/Packaging.Release/release.py")


class PublicationError(ValueError):
    def __init__(self, message, *, status=None):
        super().__init__(message)
        self.status = status


def require(condition, message):
    if not condition:
        raise PublicationError(message)


def now():
    return datetime.now(timezone.utc).isoformat()


def sha(path):
    return compat.file_hash(Path(path))


def save(path, value):
    release.atomic_json(Path(path), value)


def https_url(url, host=None):
    parsed = urlsplit(url)
    require(parsed.scheme == "https" and parsed.hostname and parsed.port in (None, 443)
            and parsed.username is None and parsed.password is None and not parsed.fragment,
            "Only credential-free HTTPS URLs on port 443 are permitted")
    if host:
        require(parsed.hostname == host, "HTTP destination differs from the permitted service")
    return parsed


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


class Http:
    """No automatic redirects; secret headers never reach a redirected host."""

    def __init__(self, opener=None):
        self.opener = opener or build_opener(NoRedirect())

    def request(self, method, url, *, credential=None, data=None, content_type=None,
                maximum=MAX_JSON):
        parsed = https_url(url)
        headers = {"User-Agent": "NativeWeave-DB-CPP-01-Publication/1.0",
                   "Accept": "application/json", "Accept-Encoding": "identity",
                   "Cache-Control": "no-cache"}
        if credential == "github":
            require(parsed.hostname == "api.github.com" and url.startswith(GITHUB + "/")
                    and method == "GET", "GitHub authorization destination rejected")
            token = os.environ.get("GITHUB_TOKEN")
            require(token and "\n" not in token and "\r" not in token, "GITHUB_TOKEN is required")
            headers.update(Authorization="Bearer " + token,
                           **{"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"})
        elif credential == "nuget":
            require(url == PUBLISH and method == "PUT" or
                    url.startswith(PUBLISH + "/" + RETIRED + "/") and method == "DELETE",
                    "NuGet credential destination or operation rejected")
            https_url(url, "www.nuget.org")
            token = os.environ.get("NUGET_API_KEY")
            require(token and "\n" not in token and "\r" not in token, "NUGET_API_KEY is required")
            headers.update({"X-NuGet-ApiKey": token, "X-NuGet-Protocol-Version": "4.1.0"})
        else:
            require(credential is None, "Unknown credential kind")
        if content_type:
            headers["Content-Type"] = content_type
        request = Request(url, data=data, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=60)
        except HTTPError as error:
            response = error
        except (URLError, TimeoutError, OSError):
            # Do not print exception URLs, response bodies, headers or tokens.
            raise PublicationError("HTTP transport failed; a mutation's outcome may be unknown") from None
        with response:
            status = response.code
            response_headers = dict(response.headers.items())
            if credential == "nuget" and 300 <= status < 400:
                raise PublicationError("Refused a redirect carrying NuGet credentials", status=status)
            body = response.read(maximum + 1)
            require(len(body) <= maximum, "HTTP response exceeds its bounded profile")
            encoding = response.headers.get("Content-Encoding", "identity").lower()
            if encoding == "gzip":
                with gzip.GzipFile(fileobj=io.BytesIO(body)) as stream:
                    body = stream.read(maximum + 1)
                require(len(body) <= maximum, "Expanded HTTP response exceeds its bounded profile")
            else:
                require(encoding in ("", "identity"), "Unsupported HTTP content encoding")
        return status, response_headers, body

    def json(self, url, *, credential=None, optional=False):
        status, _, body = self.request("GET", url, credential=credential)
        if optional and status == 404:
            return None
        if status != 200:
            raise PublicationError("JSON request failed with HTTP " + str(status), status=status)
        return json.loads(body)


def load_manifest(path):
    value = json.loads(Path(path).read_text())
    require(value.get("schemaVersion") == 1, "Unknown publication manifest schema")
    for key, expected in PINNED.items():
        require(value.get(key) == expected, "Publication manifest differs from accepted " + key)
    if "sourceTree" in value:
        require(value["sourceTree"] == "3264de79b73c8a89f1cac1eff57d0c0ff2c841c2", "Unexpected accepted source tree")
    expected_versions = domain.versions("0.1.0-db-cpp.1.1", "0.1.0-db-cpp.1.2")
    require(value.get("packageVersions") == expected_versions, "Publication package IDs or versions differ")
    hashes = value.get("packageArchiveSha256", {})
    require(set(hashes) == set(expected_versions)
            and all(isinstance(item, str) and re.fullmatch(r"[0-9a-f]{64}", item) for item in hashes.values()),
            "Every original package archive needs its reviewed SHA-256")
    return value


def validate_github(manifest, run, artifact, commit=None):
    require(run.get("id") == manifest["runId"] and run.get("head_sha") == manifest["sourceCommit"]
            and run.get("head_branch") == "main" and run.get("event") == "push"
            and run.get("status") == "completed" and run.get("conclusion") == "success"
            and run.get("run_attempt") == 1 and run.get("path") == ".github/workflows/ci.yml"
            and run.get("repository", {}).get("full_name") == manifest["repository"]
            and run.get("repository", {}).get("id") == 1454887692,
            "GitHub run is not the accepted successful main CI attempt")
    workflow = artifact.get("workflow_run", {})
    require(artifact.get("id") == manifest["artifactId"] and artifact.get("name") == manifest["artifactName"]
            and artifact.get("size_in_bytes") == manifest["artifactSize"] and artifact.get("expired") is False
            and artifact.get("digest") == "sha256:" + manifest["artifactSha256"]
            and workflow.get("id") == manifest["runId"] and workflow.get("head_sha") == manifest["sourceCommit"]
            and workflow.get("head_branch") == "main" and workflow.get("repository_id") == 1454887692,
            "GitHub artifact identity, digest, source or availability differs")
    if "sourceTree" in manifest:
        require(commit and commit.get("sha") == manifest["sourceCommit"]
                and commit.get("tree", {}).get("sha") == manifest["sourceTree"], "GitHub source tree differs")
    return {"observedAt": now(), "liveGitHubApi": True, "runId": run["id"],
            "runUrl": "https://github.com/" + manifest["repository"] + "/actions/runs/" + str(run["id"]),
            "runAttempt": run["run_attempt"], "runConclusion": run["conclusion"],
            "sourceCommit": run["head_sha"], "artifactId": artifact["id"],
            "artifactName": artifact["name"], "artifactDigest": artifact["digest"],
            "artifactSize": artifact["size_in_bytes"]}


def artifact_redirect(url):
    host = https_url(url).hostname
    require(host.endswith(".blob.core.windows.net") or host.endswith(".actions.githubusercontent.com")
            or host == "objects.githubusercontent.com" or host.endswith(".s3.amazonaws.com")
            or re.fullmatch(r"[a-z0-9.-]+\.s3\.[a-z0-9-]+\.amazonaws\.com", host),
            "Unexpected GitHub artifact storage host")


def download_artifact(http, manifest, path):
    url = GITHUB + "/actions/artifacts/" + str(manifest["artifactId"]) + "/zip"
    credential = "github"
    for _ in range(5):
        status, headers, body = http.request("GET", url, credential=credential, maximum=manifest["artifactSize"])
        if status == 200:
            require(len(body) == manifest["artifactSize"]
                    and hashlib.sha256(body).hexdigest() == manifest["artifactSha256"], "Artifact archive digest differs")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(body)
            return
        require(status in (301, 302, 303, 307, 308), "GitHub artifact download failed")
        location = next((value for key, value in headers.items() if key.lower() == "location"), None)
        require(location is not None, "Artifact redirect has no location")
        artifact_redirect(location)
        url, credential = location, None
    raise PublicationError("Too many GitHub artifact redirects")


def stage_archive(manifest, archive_path, work):
    require(archive_path.is_file() and archive_path.stat().st_size == manifest["artifactSize"]
            and sha(archive_path) == manifest["artifactSha256"], "Original artifact archive identity differs")
    prefix = manifest["candidatePath"] + "/"
    target = work / "source" / manifest["candidatePath"]
    require(target.resolve().is_relative_to(work.resolve()), "Staging symlink escapes the publication directory")
    names, selected = set(), {}
    with zipfile.ZipFile(archive_path) as archive:
        require(len(archive.infolist()) <= 20000, "Too many artifact entries")
        expanded = 0
        for entry in archive.infolist():
            name = entry.filename.rstrip("/") if entry.is_dir() else entry.filename
            compat.canonical_path(name)
            require(name.lower() not in names, "Duplicate artifact path")
            names.add(name.lower())
            require((entry.external_attr >> 16) & 0o170000 != 0o120000, "Artifact symlink rejected")
            expanded += entry.file_size
            require(entry.file_size <= MAX_PACKAGE and expanded <= 128 * 1024 * 1024,
                    "Artifact expansion exceeds the bounded profile")
            if not entry.is_dir() and name.startswith(prefix):
                selected[name[len(prefix):]] = archive.read(entry)
    expected_names = {"release.json"} | {
        "feed/" + identity + "." + version + ".nupkg" for identity, version in manifest["packageVersions"].items()}
    require(set(selected) == expected_names, "Candidate archive contains missing or undeclared files")
    for name, content in selected.items():
        path = domain.relative_file(target, name)
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists():
            require(path.is_file() and path.read_bytes() == content, "Existing staged bytes differ from the original artifact")
        else:
            path.write_bytes(content)
    record = json.loads((target / "release.json").read_text())
    require(record.get("schema_version") == 1 and record.get("source_commit") == manifest["sourceCommit"]
            and record.get("source_dirty") is False and record.get("rid") == "linux-x64"
            and record.get("local_unsigned_only") is True
            and record.get("builder_identity") == "nativeweave-local-reviewed-staging",
            "Original candidate provenance differs; local-only history must remain unchanged")
    require({item["id"]: item["version"] for item in record["packages"]} == manifest["packageVersions"]
            and len(record["packages"]) == len(manifest["packageVersions"]), "Candidate package closure differs")
    packages = {}
    for item in record["packages"]:
        path = target / "feed" / (item["id"] + "." + item["version"] + ".nupkg")
        require(sha(path) == item["upload_archive_sha256"] == manifest["packageArchiveSha256"][item["id"]],
                "Original package archive digest differs: " + item["id"])
        require(item.get("source_commit") == manifest["sourceCommit"] and item.get("repository") == domain.REPOSITORY,
                "Package source provenance differs: " + item["id"])
        domain.validate_database_payload(path, item)
        packages[item["id"]] = path
    compat.validate_closure(record, packages)
    return record, packages


def preflight(manifest, manifest_path, work, http, archive_path=None):
    run = http.json(GITHUB + "/actions/runs/" + str(manifest["runId"]), credential="github")
    artifact = http.json(GITHUB + "/actions/artifacts/" + str(manifest["artifactId"]), credential="github")
    commit = http.json(GITHUB + "/git/commits/" + manifest["sourceCommit"], credential="github") if "sourceTree" in manifest else None
    proof = validate_github(manifest, run, artifact, commit)
    archive_path = archive_path or work / "accepted-artifact.zip"
    if not archive_path.exists():
        download_artifact(http, manifest, archive_path)
    record, packages = stage_archive(manifest, archive_path, work)
    save(work / "preflight.json", {"schemaVersion": 1, "passed": True, "observedAt": now(),
         "manifestSha256": sha(manifest_path), "source": proof, "rebuiltOrRepacked": False,
         "publicPublicationClaimed": False, "packageArchiveSha256": {name: sha(path) for name, path in packages.items()}})
    print("Preflight: verified accepted main CI, original artifact and all 12 package payloads", flush=True)
    return record, packages


def service_resources(http):
    document = http.json(INDEX)
    resources = {}
    for wanted in ("PackagePublish/2.0.0", "PackageBaseAddress/3.0.0", "RegistrationsBaseUrl/3.6.0", "RepositorySignatures/5.0.0"):
        matches = [item["@id"] for item in document["resources"] if item.get("@type") == wanted]
        require(len(set(matches)) == 1, "NuGet service index has an ambiguous or missing resource")
        url = matches[0]
        if wanted == "PackagePublish/2.0.0":
            require(url == PUBLISH, "NuGet publish endpoint differs from the fixed destination")
        else:
            https_url(url, "api.nuget.org")
        resources[wanted] = url
    return resources


def fetch_signature_policies(owner, work, http, resources=None):
    require(owner == OWNER, "Unpermitted package owner")
    resources = resources or service_resources(http)
    url = resources["RepositorySignatures/5.0.0"]
    status, _, raw = http.request("GET", url)
    require(status == 200, "Unable to retrieve official repository signing policy")
    value = json.loads(raw)
    require(value.get("allRepositorySigned") is True, "NuGet must assert repository signatures for all packages")
    policies = []
    for certificate in value.get("signingCertificates", []):
        fingerprint = certificate.get("fingerprints", {}).get("2.16.840.1.101.3.4.2.1", "")
        subject = certificate.get("subject", "")
        require(re.fullmatch(r"[0-9a-f]{64}", fingerprint) and subject and "\n" not in subject,
                "Invalid official repository signing certificate")
        policies.append({"route": "repository-primary", "service_index": INDEX, "owners": [owner],
                         "certificate_sha256": fingerprint, "subject": subject})
    require(policies and len({row["certificate_sha256"] for row in policies}) == len(policies),
            "Empty or duplicate official signing certificate set")
    save(work / "signature-policy.json", {"schemaVersion": 1, "observedAt": now(),
         "repositorySignaturesUrl": url, "repositorySignaturesSha256": hashlib.sha256(raw).hexdigest(),
         "service_index": INDEX, "owners": [owner], "policies": policies})
    return policies


def signature_policy_from_output(output, policies):
    require(re.findall(r"^Signature type: (.+)$", output, re.M) == ["Repository"], "Unexpected primary signature route")
    require(re.findall(r"^Service index: (.+)$", output, re.M) == [INDEX], "Unexpected signed repository service index")
    owners = re.findall(r"^Owners: (.+)$", output, re.M)
    require(len(owners) == 1 and sorted(item.strip() for item in owners[0].split(",")) == [OWNER],
            "Signed repository ownership differs from dekueon")
    matches = list(re.finditer(r"Verifying the repository primary signature with certificate:\s*\n"
                              r"  Subject Name: (?P<subject>[^\n]+)\n  SHA1 hash: [^\n]+\n  SHA256 hash: (?P<hash>[0-9A-F]+)", output))
    require(len(matches) == 1, "Missing or ambiguous primary repository certificate")
    match = matches[0]
    selected = [policy for policy in policies if policy["certificate_sha256"] == match["hash"].lower()
                and policy["subject"] == match["subject"] and policy["owners"] == [OWNER]
                and policy["service_index"] == INDEX and policy["route"] == "repository-primary"]
    require(len(selected) == 1, "Primary repository certificate is absent from the official signing policy")
    return selected[0]


@contextmanager
def without_credentials():
    # The unchanged verifier starts dotnet itself. Do not expose publication
    # credentials to that read-only child process or its optional plugins.
    names = [name for name in os.environ if name in ("NUGET_API_KEY", "GITHUB_TOKEN", "GH_TOKEN")
             or name.startswith("ACTIONS_") and ("TOKEN" in name or "URL" in name)]
    held = {name: os.environ.pop(name) for name in names}
    try:
        yield
    finally:
        os.environ.update(held)


def verify_readback(original, downloaded, expected, policies, logs):
    compat.package_payload(downloaded, expected)
    domain.validate_database_payload(downloaded, expected)
    logs.mkdir(parents=True, exist_ok=True)
    with without_credentials():
        result = subprocess.run(["dotnet", "nuget", "verify", str(downloaded.resolve()), "--all",
                                 "--verbosity", "detailed", "--force-english-output"],
                                capture_output=True, text=True, timeout=120)
        (logs / "discover.stdout").write_text(result.stdout)
        (logs / "discover.stderr").write_text(result.stderr)
        require(result.returncode == 0, "NuGet cryptographic repository signature verification failed")
        policy = signature_policy_from_output(result.stdout, policies)
        verification = compat.verify_existing(original, downloaded, expected, policy, logs / "pinned")
    return {"verification": verification, "signaturePolicy": policy, "registrySha256": sha(downloaded)}


def package_url(resources, identity, version):
    require(identity in domain.IDS or identity == RETIRED, "Unpermitted package identity")
    require(re.fullmatch(r"[0-9A-Za-z.-]+", version), "Unpermitted package version")
    base = resources["PackageBaseAddress/3.0.0"].rstrip("/")
    return base + "/" + identity.lower() + "/" + version.lower() + "/" + identity.lower() + "." + version.lower() + ".nupkg"


def download_package(http, url, path):
    https_url(url, "api.nuget.org")
    status, _, body = http.request("GET", url, maximum=MAX_PACKAGE)
    if status == 404:
        return False
    if status != 200:
        raise PublicationError("Registry package readback failed with HTTP " + str(status), status=status)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(body)
    return True


def registration(http, resources, identity):
    require(identity in domain.IDS or identity == RETIRED, "Unpermitted registration identity")
    url = resources["RegistrationsBaseUrl/3.6.0"].rstrip("/") + "/" + identity.lower() + "/index.json"
    document = http.json(url, optional=True)
    if document is None:
        return {}, url
    pages = document.get("items", [])
    require(isinstance(pages, list) and len(pages) <= 1024, "Invalid registration page count")
    result = {}
    for page in pages:
        if "items" not in page:
            https_url(page["@id"], "api.nuget.org")
            page = http.json(page["@id"])
        for item in page.get("items", []):
            value = item["catalogEntry"]
            require(value["id"].lower() == identity.lower(), "Registration package identity differs")
            version = value["version"].lower()
            require(re.fullmatch(r"[0-9a-z.-]+", version) and version not in result, "Invalid or duplicate registry version")
            listed = value.get("listed", True)
            require(type(listed) is bool, "Invalid registration listed flag")
            result[version] = listed
    return result, url


def upload(http, path):
    boundary = "nativeweave-" + secrets.token_hex(24)
    body = ("--" + boundary + '\r\nContent-Disposition: form-data; name="package"; filename="package.nupkg"\r\n'
            + "Content-Type: application/octet-stream\r\n\r\n").encode() + path.read_bytes() + ("\r\n--" + boundary + "--\r\n").encode()
    status, _, _ = http.request("PUT", PUBLISH, credential="nuget", data=body,
                                content_type="multipart/form-data; boundary=" + boundary)
    if status not in (201, 202, 409):
        raise PublicationError("NuGet upload failed with HTTP " + str(status), status=status)
    return status


def publish(manifest, manifest_path, work, http, archive_path=None):
    record, packages = preflight(manifest, manifest_path, work, http, archive_path)
    resources = service_resources(http)
    policies = fetch_signature_policies(manifest["packageOwner"], work, http, resources)
    journal_path = work / "publication.json"
    journal = {"schemaVersion": 1, "status": "incomplete", "startedAt": now(), "publicPublicationComplete": False,
               "sourceCommit": manifest["sourceCommit"], "owner": OWNER, "destination": INDEX,
               "manifestSha256": sha(manifest_path), "artifactSha256": manifest["artifactSha256"],
               "rebuiltOrRepacked": False, "packages": []}
    save(journal_path, journal)
    ordered = release.order(record)
    try:
        # Check every pre-existing version before the first irreversible upload.
        for item in ordered:
            identity = item["id"]
            url = package_url(resources, identity, item["version"])
            downloaded = work / "registry" / (identity + "." + item["version"] + ".nupkg")
            row = {"id": identity, "version": item["version"], "uploadSha256": sha(packages[identity]),
                   "registryUrl": url, "status": "absent-before-upload"}
            journal["packages"].append(row)
            save(journal_path, journal)
            if download_package(http, url, downloaded):
                row.update(verify_readback(packages[identity], downloaded, item, policies, work / "signatures" / identity))
                row["status"] = "existing-payload-and-signature-verified"
            save(journal_path, journal)
        require(os.environ.get("NUGET_API_KEY") or all(row["status"].startswith("existing-") for row in journal["packages"]),
                "NUGET_API_KEY is required for absent packages")
        for item, row in zip(ordered, journal["packages"]):
            if row["status"].startswith("existing-"):
                continue
            # Recheck source bytes immediately before handing them to the registry.
            require(sha(packages[item["id"]]) == item["upload_archive_sha256"], "Upload archive changed after preflight")
            row["status"] = "upload-started-outcome-unconfirmed"
            save(journal_path, journal)
            status = upload(http, packages[item["id"]])
            row.update(uploadHttpStatus=status, status="accepted-awaiting-readback" if status != 409 else "conflict-awaiting-readback")
            save(journal_path, journal)
            print("NuGet upload accepted for readback: " + item["id"] + " " + item["version"], flush=True)
        deadline = time.monotonic() + WAIT_SECONDS
        while True:
            pending = 0
            for item, row in zip(ordered, journal["packages"]):
                if row["status"] == "public-readback-verified":
                    continue
                identity = item["id"]
                downloaded = work / "registry" / (identity + "." + item["version"] + ".nupkg")
                if not download_package(http, row["registryUrl"], downloaded):
                    pending += 1
                    continue
                row.update(verify_readback(packages[identity], downloaded, item, policies, work / "signatures" / identity))
                listed, url = registration(http, resources, identity)
                row.update(registrationUrl=url, listed=listed.get(item["version"].lower()))
                if row["listed"] is True:
                    row["status"] = "public-readback-verified"
                    print("NuGet signed public readback verified: " + identity, flush=True)
                else:
                    pending += 1
                save(journal_path, journal)
            if not pending:
                break
            require(time.monotonic() < deadline, "NuGet publication readback or listing is still incomplete after the bounded wait")
            print("Waiting for NuGet indexing/signing: " + str(pending) + " packages", flush=True)
            time.sleep(POLL_SECONDS)
        registry = {item["id"]: work / "registry" / (item["id"] + "." + item["version"] + ".nupkg") for item in ordered}
        compat.validate_closure(record, registry)
        journal.update(status="complete", publicPublicationComplete=True, completedAt=now())
        save(journal_path, journal)
        return journal
    except Exception as error:
        journal.update(status="incomplete", failedAt=now(), failureType=type(error).__name__)
        if isinstance(error, PublicationError):
            journal.update(failure=str(error), httpStatus=error.status)
        save(journal_path, journal)
        raise


def retired_versions(http, resources):
    url = resources["PackageBaseAddress/3.0.0"].rstrip("/") + "/" + RETIRED.lower() + "/index.json"
    value = http.json(url, optional=True)
    versions = [] if value is None else value.get("versions", [])
    require(isinstance(versions, list) and len(versions) <= 4096
            and len(set(versions)) == len(versions)
            and all(isinstance(version, str) and re.fullmatch(r"[0-9a-z.-]+", version) for version in versions),
            "Invalid retired package version inventory")
    return versions, url


def unlist_helloworld(manifest, manifest_path, work, http):
    require(manifest["retiredPackageId"] == RETIRED and manifest["packageOwner"] == OWNER,
            "Retired package or owner differs from authorization")
    resources = service_resources(http)
    versions, inventory_url = retired_versions(http, resources)
    listing, registration_url = registration(http, resources, RETIRED)
    receipt_path = work / "helloworld-unlist.json"
    receipt = {"schemaVersion": 1, "status": "incomplete", "allVersionsUnlisted": False, "startedAt": now(),
               "packageId": RETIRED, "owner": OWNER, "manifestSha256": sha(manifest_path),
               "inventoryUrl": inventory_url, "registrationUrl": registration_url,
               "hardDeletionClaimed": False, "publicDatabasePublicationUnaffected": True, "versions": []}
    save(receipt_path, receipt)
    try:
        require(set(listing) == set(versions), "Retired package content and registration inventories disagree")
        for version in versions:
            row = {"version": version, "listedBefore": listing.get(version), "status": "pending"}
            receipt["versions"].append(row)
            save(receipt_path, receipt)
            if listing.get(version) is False:
                row["status"] = "already-unlisted"
            else:
                row["status"] = "unlist-started-outcome-unconfirmed"
                save(receipt_path, receipt)
                status, _, _ = http.request("DELETE", PUBLISH + "/" + RETIRED + "/" + version, credential="nuget")
                row["httpStatus"] = status
                if status == 204:
                    row["status"] = "accepted-awaiting-registration"
                elif status in (401, 403):
                    row.update(status="authorization-rejected", reason="NuGet rejected unlisting authorization; publishing and unlisting scopes can differ")
                else:
                    row["status"] = "unlist-not-confirmed"
            save(receipt_path, receipt)
        deadline = time.monotonic() + WAIT_SECONDS
        while True:
            current_versions, _ = retired_versions(http, resources)
            listing, _ = registration(http, resources, RETIRED)
            pending = []
            for row in receipt["versions"]:
                row["listedAfter"] = listing.get(row["version"])
                if row["listedAfter"] is False:
                    row["status"] = "registration-confirms-unlisted"
                elif row["status"] == "accepted-awaiting-registration":
                    pending.append(row)
            receipt["finalObservedVersions"] = current_versions
            receipt["finalRegistrationVersions"] = sorted(listing)
            receipt["unattemptedVersions"] = sorted(set(current_versions) - set(versions))
            complete = not receipt["unattemptedVersions"] and set(current_versions) == set(versions) == set(listing) and all(
                row.get("listedAfter") is False for row in receipt["versions"])
            receipt.update(allVersionsUnlisted=complete, status="complete" if complete else "incomplete", observedAt=now())
            save(receipt_path, receipt)
            if complete or not pending or time.monotonic() >= deadline:
                break
            print("Waiting for HelloWorld unlisting in the public registration catalog", flush=True)
            time.sleep(POLL_SECONDS)
        require(receipt["allVersionsUnlisted"], "HelloWorld unlisting remains incomplete; see the separate receipt for authorization and listed flags")
        return receipt
    except Exception as error:
        receipt.update(status="incomplete", failedAt=now(), failureType=type(error).__name__)
        if isinstance(error, PublicationError):
            receipt.update(failure=str(error), httpStatus=error.status)
        save(receipt_path, receipt)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("preflight", "publish", "unlist-helloworld"))
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--archive", type=Path, help="Reuse the exact original ZIP; GitHub identity is still checked live")
    args = parser.parse_args()
    try:
        manifest = load_manifest(args.manifest)
        args.work_dir.mkdir(parents=True, exist_ok=True)
        http = Http()
        if args.command == "preflight":
            preflight(manifest, args.manifest, args.work_dir, http, args.archive)
        elif args.command == "publish":
            publish(manifest, args.manifest, args.work_dir, http, args.archive)
        else:
            require(args.archive is None, "Unlisting does not use a candidate archive")
            unlist_helloworld(manifest, args.manifest, args.work_dir, http)
    except PublicationError as error:
        print("Publication incomplete: " + str(error), file=sys.stderr)
        return 1
    except Exception as error:
        # Arbitrary exception text can contain a signed download URL or a
        # credential-bearing HTTP detail. Persist only safe, structured status.
        print("Publication incomplete: " + type(error).__name__ + "; inspect the structured receipt", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
