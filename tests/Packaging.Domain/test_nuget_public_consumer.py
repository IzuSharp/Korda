"""Rejection controls for public provenance checks; these never execute an app.

Fixtures are deliberately synthetic. A test pass is not publication or public
PackageReference consumption evidence; the four real executions remain required.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location(
    "nuget_public_consumer", Path(__file__).resolve().parents[2] / "eng/verify_nuget_public.py")
PUBLIC = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLIC)


class PublicConsumerProvenanceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source_commit = "a" * 40

    def package(self, *, changed_payload=False, signed=True):
        nuspec = ("<package><metadata><id>NativeWeave.Data</id><version>0.1.0-db-cpp.1.1</version>"
                  '<repository type="git" url="https://github.com/IzuSharp/NativeWeave" commit="'
                  + self.source_commit + '" /></metadata></package>').encode()
        payloads = {"NativeWeave.Data.nuspec": nuspec, "lib/net11.0/NativeWeave.Data.dll": b"unexecuted fixture"}
        expected = {"id": "NativeWeave.Data", "version": PUBLIC.COMMON_VERSION,
                    "source_commit": self.source_commit, "dependencies": {},
                    "unsigned_entries": {name: hashlib.sha256(value).hexdigest() for name, value in payloads.items()}}
        path = self.root / "fixture.nupkg"
        with zipfile.ZipFile(path, "w") as archive:
            for name, value in payloads.items():
                archive.writestr(name, b"different fixture" if changed_payload and name.endswith(".dll") else value)
            if signed:
                archive.writestr(".signature.p7s", b"synthetic placeholder, not a valid cryptographic signature")
        return path, expected

    def test_repository_signature_may_change_archive_but_not_payload(self):
        path, expected = self.package()
        receipt = PUBLIC.validate_payload(path, expected)
        self.assertTrue(receipt["accepted_unsigned_entries_equal"])
        # Payload validation cannot certify a signature or claim application execution.
        self.assertNotIn("signature", receipt)
        self.assertNotIn("passed", receipt)
        path, expected = self.package(changed_payload=True)
        with self.assertRaisesRegex(ValueError, "payload differs"):
            PUBLIC.validate_payload(path, expected)

    def test_unsigned_local_archive_is_not_a_public_package(self):
        path, expected = self.package(signed=False)
        with self.assertRaisesRegex(ValueError, "not repository signed"):
            PUBLIC.validate_payload(path, expected)

    def graph(self, *, source=PUBLIC.SOURCE, extra_package=False, fallback=False):
        cache = self.root / "cache/nativeweave.data/0.1.0-db-cpp.1.1"
        cache.mkdir(parents=True, exist_ok=True)
        (cache / ".nupkg.metadata").write_text(json.dumps({"version": 2, "source": source, "contentHash": "fixture"}))
        assets = {"project": {"restore": {"sources": {PUBLIC.SOURCE: {}}}},
                  "packageFolders": {str(self.root / "cache"): {}},
                  "libraries": {"NativeWeave.Data/0.1.0-db-cpp.1.1": {"type": "package"}}}
        if extra_package:
            assets["libraries"]["NativeWeave.Database.PostgreSql.Native/0.1.0-db-cpp.1.2"] = {"type": "package"}
        if fallback:
            assets["project"]["restore"]["fallbackFolders"] = ["/unaccepted-sdk-cache"]
        (self.root / "obj").mkdir(exist_ok=True)
        (self.root / "obj/project.assets.json").write_text(json.dumps(assets))
        return {"NativeWeave.Data": {"version": PUBLIC.COMMON_VERSION}}

    def test_local_cache_origin_cannot_masquerade_as_public_restore(self):
        selected = self.graph()
        self.assertEqual(PUBLIC.restored_graph(self.root, selected)["sources"], [PUBLIC.SOURCE])
        self.graph(source="/local/feed")
        with self.assertRaisesRegex(ValueError, "did not originate"):
            PUBLIC.restored_graph(self.root, selected)

    def test_other_provider_or_sdk_fallback_is_rejected(self):
        selected = self.graph(extra_package=True)
        with self.assertRaisesRegex(ValueError, "closure differs"):
            PUBLIC.restored_graph(self.root, selected)
        selected = self.graph(fallback=True)
        with self.assertRaisesRegex(ValueError, "fallback"):
            PUBLIC.restored_graph(self.root, selected)

    def signature(self, owner="dekueon", certificate="b" * 64):
        policy = {"route": "repository-primary", "service_index": PUBLIC.SOURCE, "owners": ["dekueon"],
                  "certificate_sha256": "b" * 64, "subject": "CN=NuGet.org test fixture"}
        output = ("Signature type: Repository\nService index: " + PUBLIC.SOURCE + "\nOwners: " + owner
                  + "\nVerifying the repository primary signature with certificate:\n"
                  + "  Subject Name: CN=NuGet.org test fixture\n  SHA1 hash: 1234\n  SHA256 hash: " + certificate + "\n")
        return output, [policy]

    def test_wrong_owner_or_unlisted_certificate_is_rejected(self):
        output, policies = self.signature()
        self.assertEqual(PUBLIC.signature_identity(output, policies)["owners"], ["dekueon"])
        for output, policies in (self.signature(owner="unrelated"), self.signature(certificate="c" * 64)):
            with self.assertRaisesRegex(ValueError, "outside the official policy"):
                PUBLIC.signature_identity(output, policies)

    def test_signature_service_and_route_are_not_inferred_from_source_url(self):
        output, policies = self.signature()
        with self.assertRaisesRegex(ValueError, "primary signature"):
            PUBLIC.signature_identity(output.replace("Signature type: Repository", "Signature type: Author"), policies)
        with self.assertRaisesRegex(ValueError, "service index differs"):
            PUBLIC.signature_identity(output.replace(PUBLIC.SOURCE, "https://example.invalid/index.json"), policies)

    def business(self, *, aot=True, remove=None, native_hash="d" * 64):
        checks = set(PUBLIC.CHECKS) | {"actual-native-aot"}
        if remove:
            checks.remove(remove)
        receipt = {"schemaVersion": 1, "task": "DB-CPP-01", "provider": "sqlite",
                   "dynamicCodeSupported": not aot, "checks": sorted(checks),
                   "nativeLibraries": [{"file": "libnw_database_sqlite.so", "sha256": native_hash}]}
        index = {"NativeWeave.Database.Sqlite.Native": {"native": {"sha256": "d" * 64}}}
        return "B24_RESULT " + json.dumps(receipt), index

    def test_jit_result_or_missing_retirement_cannot_pass_aot_cell(self):
        text, index = self.business()
        self.assertFalse(PUBLIC.business_receipt(text, "sqlite", "aot", index)["dynamicCodeSupported"])
        text, index = self.business(aot=False)
        with self.assertRaisesRegex(ValueError, "mode differs"):
            PUBLIC.business_receipt(text, "sqlite", "aot", index)
        text, index = self.business(remove="database-local-owners-retired")
        with self.assertRaisesRegex(ValueError, "retirement checks"):
            PUBLIC.business_receipt(text, "sqlite", "aot", index)

    def test_wrong_actually_mapped_library_is_rejected(self):
        text, index = self.business(native_hash="e" * 64)
        with self.assertRaisesRegex(ValueError, "Actually mapped"):
            PUBLIC.business_receipt(text, "sqlite", "aot", index)

    def test_consumer_does_not_inherit_publication_or_runner_credentials(self):
        credentials = {name: "synthetic-test-only" for name in (
            "NUGET_API_KEY", "GITHUB_TOKEN", "GH_TOKEN", "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
            "ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_RUNTIME_TOKEN")}
        original = {**credentials, "CI": "true", "RUNNER_OS": "Linux", "PATH": "/usr/bin"}
        consumer = PUBLIC.PublicConsumer.__new__(PUBLIC.PublicConsumer)
        with patch.dict(os.environ, original, clear=True):
            child = consumer.environment(self.root)
            self.assertTrue(set(credentials).isdisjoint(child))
            self.assertEqual(child["CI"], "true")
            self.assertEqual(child["RUNNER_OS"], "Linux")
            self.assertEqual(child["PATH"], "/usr/bin")
            self.assertEqual(child["NUGET_PACKAGES"], str(self.root / "cache"))
            self.assertEqual(dict(os.environ), original)


if __name__ == "__main__":
    unittest.main()
