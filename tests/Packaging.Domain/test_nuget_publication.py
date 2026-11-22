#!/usr/bin/env python3
"""Refusal and partial-outcome tests for the public, irreversible registry adapter."""
from __future__ import annotations

from contextlib import ExitStack
import copy
import gzip
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("test_public_nuget_adapter", ROOT / "eng/publish_nuget.py")
publication = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publication)


class Response(io.BytesIO):
    def __init__(self, body=b"", status=200, headers=None):
        super().__init__(body)
        self.code = status
        self.headers = headers or {}


class PublicPublicationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)
        quiet = patch.object(publication, "print", create=True)
        quiet.start()
        self.addCleanup(quiet.stop)
        self.manifest_path = ROOT / "packaging/releases/DB-CPP-01-nuget.json"
        self.manifest = publication.load_manifest(self.manifest_path)
        self.resources = {
            "PackagePublish/2.0.0": publication.PUBLISH,
            "PackageBaseAddress/3.0.0": "https://api.nuget.org/v3-flatcontainer/",
            "RegistrationsBaseUrl/3.6.0": "https://api.nuget.org/v3/registration5-gz-semver2/",
            "RepositorySignatures/5.0.0": "https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json",
        }

    def github_identity(self):
        run = {"id": self.manifest["runId"], "head_sha": self.manifest["sourceCommit"],
               "head_branch": "main", "event": "push", "status": "completed", "conclusion": "success",
               "run_attempt": 1, "path": ".github/workflows/ci.yml",
               "repository": {"full_name": "IzuSharp/NativeWeave", "id": 1454887692}}
        artifact = {"id": self.manifest["artifactId"], "name": self.manifest["artifactName"],
                    "size_in_bytes": self.manifest["artifactSize"], "expired": False,
                    "digest": "sha256:" + self.manifest["artifactSha256"],
                    "workflow_run": {"id": self.manifest["runId"], "head_sha": self.manifest["sourceCommit"],
                                     "head_branch": "main", "repository_id": 1454887692}}
        commit = {"sha": self.manifest["sourceCommit"], "tree": {"sha": self.manifest["sourceTree"]}}
        return run, artifact, commit

    def test_run_must_be_exact_successful_main_attempt(self):
        run, artifact, commit = self.github_identity()
        publication.validate_github(self.manifest, run, artifact, commit)
        for field, value in {"id": 1, "head_sha": "0" * 40, "head_branch": "topic", "event": "pull_request",
                             "status": "in_progress", "conclusion": "failure", "run_attempt": 2,
                             "path": ".github/workflows/release.yml"}.items():
            with self.subTest(field=field):
                altered = dict(run, **{field: value})
                with self.assertRaises(publication.PublicationError):
                    publication.validate_github(self.manifest, altered, artifact, commit)

    def test_artifact_identity_digest_and_availability_are_mandatory(self):
        run, artifact, commit = self.github_identity()
        for field, value in {"id": 1, "name": "other", "size_in_bytes": 10, "expired": True,
                             "digest": "sha256:" + "0" * 64}.items():
            with self.subTest(field=field), self.assertRaises(publication.PublicationError):
                publication.validate_github(self.manifest, run, dict(artifact, **{field: value}), commit)
        other_source = copy.deepcopy(artifact)
        other_source["workflow_run"]["head_sha"] = "0" * 40
        with self.assertRaises(publication.PublicationError):
            publication.validate_github(self.manifest, run, other_source, commit)

    def test_manifest_cannot_redirect_publication_or_change_closure(self):
        for field, value in {"repository": "elsewhere/project", "packageOwner": "someone",
                             "excludedPackageIds": [], "candidatePath": "cpp-clean/prepared/candidate",
                             "sourceCommit": "0" * 40, "packageVersions": {}}.items():
            path = self.work / "manifest.json"
            path.write_text(json.dumps(dict(self.manifest, **{field: value})))
            with self.subTest(field=field), self.assertRaises(publication.PublicationError):
                publication.load_manifest(path)

    def test_original_archive_digest_is_checked_before_extraction(self):
        archive = self.work / "candidate.zip"
        archive.write_bytes(b"not the accepted artifact")
        with self.assertRaises(publication.PublicationError):
            publication.stage_archive(self.manifest, archive, self.work)
        self.assertFalse((self.work / "source").exists())

    def test_archive_path_escape_and_symlink_are_refused(self):
        for symlink in (False, True):
            with self.subTest(symlink=symlink):
                archive_path = self.work / "unsafe.zip"
                with zipfile.ZipFile(archive_path, "w") as archive:
                    if symlink:
                        entry = zipfile.ZipInfo("db-scope/unsafe")
                        entry.external_attr = 0o120777 << 16
                        archive.writestr(entry, "../../outside")
                    else:
                        archive.writestr("../outside", "unsafe")
                manifest = dict(self.manifest, artifactSize=archive_path.stat().st_size,
                                artifactSha256=publication.sha(archive_path))
                with self.assertRaises(ValueError):
                    publication.stage_archive(manifest, archive_path, self.work)
                self.assertFalse((self.work / "outside").exists())

    def test_nuget_credential_redirect_is_never_followed(self):
        opener = Mock()
        opener.open.return_value = Response(status=302, headers={"Location": "https://untrusted.example/upload"})
        with patch.dict(os.environ, {"NUGET_API_KEY": "test-only-secret"}):
            with self.assertRaisesRegex(publication.PublicationError, "Refused a redirect"):
                publication.Http(opener).request("PUT", publication.PUBLISH, credential="nuget", data=b"package",
                                                 package_identity=("NativeWeave.Data", "0.1.0-db-cpp.1.1"))
        self.assertEqual(opener.open.call_count, 1)
        request = opener.open.call_args.args[0]
        self.assertEqual(request.full_url, publication.PUBLISH)
        self.assertIsNone(publication.NoRedirect().redirect_request(request, None, 302, "", {}, "https://untrusted.example"))

    def test_nuget_key_cannot_be_sent_to_other_endpoints(self):
        opener = Mock()
        for url in ("https://example.org/api/v2/package", "https://www.nuget.org/api/v2/other",
                    "http://www.nuget.org/api/v2/package"):
            with self.subTest(url=url), self.assertRaises(publication.PublicationError):
                publication.Http(opener).request("PUT", url, credential="nuget", data=b"package")
        opener.open.assert_not_called()

    def test_only_package_put_can_use_nuget_credentials(self):
        opener = Mock()
        for method in ("DELETE", "POST", "GET"):
            with self.subTest(method=method), self.assertRaises(publication.PublicationError):
                publication.Http(opener).request(method, publication.PUBLISH, credential="nuget",
                                                 package_identity=("NativeWeave.Data", "0.1.0-db-cpp.1.1"))
        opener.open.assert_not_called()

    def test_helloworld_and_unknown_packages_are_rejected_before_upload(self):
        opener, http = Mock(), Mock()
        for identity in ("NativeWeave.HelloWorld", "NativeWeave.Unknown"):
            with self.subTest(identity=identity):
                with self.assertRaises(publication.PublicationError):
                    publication.Http(opener).request("PUT", publication.PUBLISH, credential="nuget", data=b"package",
                                                     package_identity=(identity, "0.1.0-db-cpp.1.1"))
                with self.assertRaises(publication.PublicationError):
                    publication.upload(http, self.work / "never-read.nupkg", {"id": identity, "version": "0.1.0-db-cpp.1.1"})
                changed = copy.deepcopy(self.manifest)
                changed["packageVersions"][identity] = "0.1.0-db-cpp.1.1"
                path = self.work / "unpermitted-manifest.json"
                path.write_text(json.dumps(changed))
                with self.assertRaises(publication.PublicationError):
                    publication.load_manifest(path)
        opener.open.assert_not_called()
        http.request.assert_not_called()

    def test_upload_requires_frozen_version_and_accepted_payload_bytes(self):
        http = Mock()
        path = self.work / "changed.nupkg"
        path.write_bytes(b"changed package")
        for expected in ({"id": "NativeWeave.Data", "version": "0.1.0-preview.1"},
                         {"id": "NativeWeave.Data", "version": "0.1.0-db-cpp.1.1", "upload_archive_sha256": "0" * 64}):
            with self.subTest(expected=expected), self.assertRaises(publication.PublicationError):
                publication.upload(http, path, expected)
        http.request.assert_not_called()

    def test_github_blob_redirect_discards_authorization(self):
        payload = b"archive bytes"
        manifest = dict(self.manifest, artifactSize=len(payload), artifactSha256=hashlib.sha256(payload).hexdigest())
        http = Mock()
        http.request.side_effect = [(302, {"Location": "https://productionresultssa.blob.core.windows.net/result?sig=private"}, b""),
                                    (200, {}, payload)]
        publication.download_artifact(http, manifest, self.work / "archive.zip")
        self.assertEqual(http.request.call_args_list[0].kwargs["credential"], "github")
        self.assertIsNone(http.request.call_args_list[1].kwargs["credential"])
        self.assertEqual((self.work / "archive.zip").read_bytes(), payload)

    def test_github_redirect_cannot_downgrade_https(self):
        http = Mock()
        http.request.return_value = (302, {"Location": "http://productionresultssa.blob.core.windows.net/archive"}, b"")
        with self.assertRaises(publication.PublicationError):
            publication.download_artifact(http, self.manifest, self.work / "archive.zip")
        self.assertEqual(http.request.call_count, 1)

    def test_real_nuget_gzip_shape_is_decoded_and_bounded(self):
        body = gzip.compress(b'{"items": [], "count": 0}')
        opener = Mock()
        opener.open.return_value = Response(body, headers={"Content-Encoding": "gzip"})
        self.assertEqual(publication.Http(opener).json("https://api.nuget.org/v3/registration5-gz-semver2/example/index.json"),
                         {"items": [], "count": 0})
        opener.open.return_value = Response(gzip.compress(b"a" * 10000), headers={"Content-Encoding": "gzip"})
        with self.assertRaisesRegex(publication.PublicationError, "Expanded HTTP"):
            publication.Http(opener).request("GET", publication.INDEX, maximum=100)

    def signature(self, owner="dekueon", fingerprint="A" * 64, service=None):
        return ("Signature type: Repository\nService index: " + (service or publication.INDEX)
                + "\nOwners: " + owner + "\nVerifying the repository primary signature with certificate:\n"
                + "  Subject Name: CN=NuGet\n  SHA1 hash: 0000\n  SHA256 hash: " + fingerprint + "\n")

    def policy(self):
        return {"route": "repository-primary", "service_index": publication.INDEX, "owners": ["dekueon"],
                "certificate_sha256": "a" * 64, "subject": "CN=NuGet"}

    def test_repository_signature_requires_exact_owner_service_and_official_certificate(self):
        policy = self.policy()
        self.assertEqual(publication.signature_policy_from_output(self.signature(), [policy]), policy)
        for output in (self.signature(owner="different"), self.signature(owner="dekueon, other"),
                       self.signature(fingerprint="B" * 64), self.signature(service="https://other.example/index.json"),
                       self.signature().replace("Signature type: Repository", "Signature type: Author")):
            with self.subTest(output=output), self.assertRaises(publication.PublicationError):
                publication.signature_policy_from_output(output, [policy])

    def test_payload_conflict_is_rejected_before_signature_process(self):
        identity, version = "NativeWeave.Data", "0.1.0-db-cpp.1.1"
        original, downloaded = self.work / "original.nupkg", self.work / "downloaded.nupkg"
        nuspec = ("<package><metadata><id>" + identity + "</id><version>" + version
                  + "</version><dependencies /></metadata></package>")
        for path, payload in ((original, b"accepted"), (downloaded, b"different")):
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr(identity + ".nuspec", nuspec)
                archive.writestr("lib/net11.0/" + identity + ".dll", payload)
        expected = {"id": identity, "version": version, "dependencies": {},
                    "unsigned_entries": publication.compat.package_entries(original)}
        with patch.object(publication.subprocess, "run") as process:
            with self.assertRaises(ValueError):
                publication.verify_readback(original, downloaded, expected, [self.policy()], self.work / "logs")
            process.assert_not_called()

    def publication_mocks(self, stack):
        record, packages = {"schema_version": 1, "packages": []}, {}
        for identity in ("NativeWeave.Data", "NativeWeave.Data.Streaming"):
            path = self.work / (identity + ".nupkg")
            path.write_bytes(identity.encode())
            packages[identity] = path
            record["packages"].append({"id": identity, "version": "0.1.0-db-cpp.1.1", "dependencies": {},
                                       "upload_archive_sha256": publication.sha(path)})
        stack.enter_context(patch.object(publication, "preflight", return_value=(record, packages)))
        stack.enter_context(patch.object(publication, "service_resources", return_value=self.resources))
        stack.enter_context(patch.object(publication, "fetch_signature_policies", return_value=[self.policy()]))
        stack.enter_context(patch.dict(os.environ, {"NUGET_API_KEY": "test-only-secret"}))
        return record, packages

    def test_late_preexisting_conflict_prevents_all_new_uploads(self):
        with ExitStack() as stack:
            self.publication_mocks(stack)
            stack.enter_context(patch.object(publication, "download_package", side_effect=[False, True]))
            stack.enter_context(patch.object(publication, "verify_readback", side_effect=publication.PublicationError("payload conflict")))
            upload = stack.enter_context(patch.object(publication, "upload"))
            with self.assertRaises(publication.PublicationError):
                publication.publish(self.manifest, self.manifest_path, self.work, Mock())
            upload.assert_not_called()
        receipt = json.loads((self.work / "publication.json").read_text())
        self.assertFalse(receipt["publicPublicationComplete"])

    def test_all_uploads_precede_indexing_wait_and_409_is_verified(self):
        events = []
        with ExitStack() as stack:
            record, _ = self.publication_mocks(stack)
            calls = iter([False, False, True, True])
            def download(*args):
                events.append("readback")
                return next(calls)
            def upload(*args):
                events.append("upload")
                return 409
            stack.enter_context(patch.object(publication, "download_package", side_effect=download))
            stack.enter_context(patch.object(publication, "upload", side_effect=upload))
            verify = stack.enter_context(patch.object(publication, "verify_readback", return_value={"registrySha256": "b" * 64}))
            stack.enter_context(patch.object(publication, "registration", return_value=({"0.1.0-db-cpp.1.1": True}, "https://api.nuget.org/example")))
            stack.enter_context(patch.object(publication.compat, "validate_closure"))
            receipt = publication.publish(self.manifest, self.manifest_path, self.work, Mock())
            self.assertEqual(verify.call_count, len(record["packages"]))
        self.assertEqual(events, ["readback", "readback", "upload", "upload", "readback", "readback"])
        self.assertTrue(receipt["publicPublicationComplete"])
        self.assertTrue(all(row["uploadHttpStatus"] == 409 and row["status"] == "public-readback-verified" for row in receipt["packages"]))

    def test_credentials_are_removed_from_signature_subprocess_scope(self):
        credentials = {"NUGET_API_KEY": "private-nuget", "GITHUB_TOKEN": "private-github", "GH_TOKEN": "private-gh",
                       "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "private-oidc", "ACTIONS_ID_TOKEN_REQUEST_URL": "https://private.example/token",
                       "ACTIONS_RUNTIME_TOKEN": "private-runtime", "ACTIONS_RUNTIME_URL": "https://private.example/runtime"}
        with patch.dict(os.environ, credentials):
            with publication.without_credentials():
                self.assertTrue(all(name not in os.environ for name in credentials))
            self.assertEqual({name: os.environ[name] for name in credentials}, credentials)


if __name__ == "__main__":
    unittest.main()
