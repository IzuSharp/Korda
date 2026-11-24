#!/usr/bin/env python3
"""Small offline byte/role/negative controls; no database or benchmark run."""
import base64
import copy
import gzip
import hashlib
import json
from pathlib import Path
import stat
import struct
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile

import evidence_archive as archive


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode()


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def campaign():
    return {"schemaVersion": 1, "task": "DB-Perf-01", "state": "preflight",
            "requestSha256": "a" * 64, "bundleManifestSha256": "b" * 64, "completedSteps": {}}


def result(provider="PostgreSql"):
    return {"SchemaVersion": 1, "Route": "korda", "Provider": provider,
            "ExecutableSha256": "c" * 64, "Completed": 1, "Failed": 0, "Rows": 1}


class MeasurementArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="db-perf-archive-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.serial = 0

    def zip(self, entries=None):
        self.serial += 1
        path = self.root / (str(self.serial) + ".zip")
        entries = entries if entries is not None else [
            ("campaign.json", encode(campaign())),
            ("runs/postgresql/short/attempt-00/result.json", encode(result())),
            ("runs/postgresql/short/attempt-00/client.stdout.log", b"DB_PERF_RESULT output=/tmp/result.json\n")]
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as z:
                for name, raw in entries:
                    z.writestr(name, raw)
        return path

    def record(self, origin, raw, compressed=None):
        compressed = gzip.compress(raw, mtime=0) if compressed is None else compressed
        path = "checkpoint/raw/" + origin + ".gz"
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(compressed)
        return {"origin": origin, "path": path, "representation": "gzip", "sha256": sha(compressed),
                "bytes": len(compressed), "original_sha256": sha(raw), "original_bytes": len(raw),
                "original_newline_count": raw.count(b"\n")}

    def capsule(self):
        path = self.zip()
        inventory = archive.audit_zip(path)
        raw = path.read_bytes()
        chunks = []
        for index, offset in enumerate(range(0, len(raw), 64)):
            block = raw[offset:offset+64]
            chunks.append({"index": index, "offset": offset, "length": len(block),
                           "record": self.record(f"capture/checkpoint-0.zip.part-{index:06d}", block)})
        value = {"schemaVersion": 1, "kind": "measurement-artifact-byte-chunks",
                 "artifact": {"repository": "IzuSharp/Korda", "runId": 123, "runAttempt": 2,
                              "jobId": 456, "artifactId": 789, "name": "db-perf-evidence-123-checkpoint-0"},
                 "archive": {"filename": "checkpoint-0.zip", "bytes": len(raw), "sha256": sha(raw)},
                 "chunks": chunks, "inventory": self.record("capture/member-inventory.json", encode(inventory))}
        return value, inventory

    def verify(self, capsule):
        receipt = self.root / "checkpoint/read-only-verification.json"
        receipt.parent.mkdir(parents=True, exist_ok=True)
        receipt.write_bytes(encode({"measurementArtifactArchive": capsule}))
        return archive.verify_capsule(self.root, receipt)

    def test_actual_member_hashes_and_expected_inventory(self):
        path = self.zip()
        observed = archive.audit_zip(path, sha(path.read_bytes()))
        self.assertEqual(observed["memberCount"], 3)
        self.assertFalse(observed["sourcePayloadArchived"])
        self.assertFalse(observed["performanceAcceptance"])
        with zipfile.ZipFile(path) as z:
            for item in observed["members"]:
                raw = z.read(item["path"])
                self.assertEqual((item["bytes"], item["sha256"], item["newlineCount"]),
                                 (len(raw), sha(raw), raw.count(b"\n")))
        self.assertEqual(archive.audit_zip(path, expected_members=observed["members"]), observed)

    def test_original_archive_hash_and_member_omission_are_rejected(self):
        path = self.zip()
        observed = archive.audit_zip(path)
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(path, "0" * 64)
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(path, expected_members=observed["members"][:-1])
        duplicate = observed["members"][:-1] + [observed["members"][0]]
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(path, expected_members=duplicate)

    def test_duplicate_and_traversing_member_names_are_rejected(self):
        for name in ["campaign.json", "../campaign.json", "/campaign.json", "runs\\postgresql\\x\\attempt-00\\result.json",
                     "runs/postgresql/x/../attempt-00/result.json", "C:/campaign.json"]:
            with self.subTest(name=name), self.assertRaises(archive.ArchiveError):
                archive.audit_zip(self.zip([("campaign.json", encode(campaign())), (name, encode(campaign()))]))

    def test_only_fixed_measurement_roles_are_accepted(self):
        for name in ["helper.py", "source/program.json", "runtime/result.json", "package.nupkg",
                     "runs/postgresql/x/attempt-00/helper.txt", "runs/sqlite/x/attempt-00/postgres.log"]:
            with self.subTest(name=name), self.assertRaises(archive.ArchiveError):
                archive.audit_zip(self.zip([("campaign.json", encode(campaign())), (name, b"data\n")]))

    def test_disguised_source_in_json_key_value_or_log_is_rejected(self):
        for field, text in [("comment", "def hidden():\n    return 1\n"),
                            ("sourceCode", "ordinary text"),
                            ("print('hidden')", "ordinary text")]:
            value = campaign(); value[field] = text
            with self.subTest(field=field), self.assertRaises(archive.ArchiveError):
                archive.audit_zip(self.zip([("campaign.json", encode(value))]))
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
                ("runs/postgresql/x/attempt-00/client.stdout.log", b"#!/usr/bin/python3\nprint('hidden')\n")]))

    def test_encoded_source_and_nested_packages_are_rejected(self):
        source = b"#!/usr/bin/python3\nprint('implementation')\n"
        nested = self.zip().read_bytes()
        for raw in [source, nested, b"\x7fELF" + b"binary data" * 10, gzip.compress(source)]:
            for value in [base64.b64encode(raw).decode(), raw.hex()]:
                with self.subTest(prefix=raw[:4], encoding=value[:10]), self.assertRaises(archive.ArchiveError):
                    doc = campaign(); doc["note"] = value
                    archive.audit_zip(self.zip([("campaign.json", encode(doc))]))

    def test_plain_elf_and_zip_renamed_to_log_are_rejected(self):
        for raw in [b"\x7fELF" + b"x" * 100, self.zip().read_bytes()]:
            with self.subTest(prefix=raw[:4]), self.assertRaises(archive.ArchiveError):
                archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
                    ("runs/postgresql/x/attempt-00/client.stdout.log", raw)]))

    def test_symlink_and_encryption_flags_are_rejected(self):
        link = zipfile.ZipInfo("runs/postgresql/x/attempt-00/client.stdout.log")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(self.zip([("campaign.json", encode(campaign())), (link, b"/etc/passwd")]))
        path = self.zip([("campaign.json", encode(campaign()))])
        raw = bytearray(path.read_bytes())
        central = raw.index(b"PK\x01\x02")
        struct.pack_into("<H", raw, 6, struct.unpack_from("<H", raw, 6)[0] | 1)
        struct.pack_into("<H", raw, central+8, struct.unpack_from("<H", raw, central+8)[0] | 1)
        path.write_bytes(raw)
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(path)

    def test_opaque_zip_comments_extra_preamble_and_trailer_are_rejected(self):
        for transform in [lambda raw: b"#!/bin/sh\n" + raw, lambda raw: raw + b"hidden implementation"]:
            path = self.zip(); path.write_bytes(transform(path.read_bytes()))
            with self.assertRaises(archive.ArchiveError):
                archive.audit_zip(path)
        path = self.zip()
        with zipfile.ZipFile(path, "a") as z:
            z.comment = b"hidden payload"
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(path)
        item = zipfile.ZipInfo("campaign.json"); item.extra = b"\xfe\xca\x06\x00hidden"
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(self.zip([(item, encode(campaign()))]))

    def test_deflate_trailing_source_inside_consistent_member_sizes_is_rejected(self):
        path = self.zip([("campaign.json", encode(campaign()))])
        with zipfile.ZipFile(path) as z:
            central = z.start_dir
            compressed = z.getinfo("campaign.json").compress_size
        payload = b"#!/usr/bin/python3\nprint('hidden source after deflate')\n"
        raw = bytearray(path.read_bytes())
        raw[central:central] = payload
        new_central = central + len(payload)
        struct.pack_into("<I", raw, 18, compressed + len(payload))
        struct.pack_into("<I", raw, new_central + 20, compressed + len(payload))
        struct.pack_into("<I", raw, len(raw) - 22 + 16, new_central)
        path.write_bytes(raw)
        # Ordinary ZipFile returns the harmless JSON and ignores the concealed
        # bytes: member hash/inventory checks alone would miss this payload.
        with zipfile.ZipFile(path) as z:
            self.assertEqual(z.read("campaign.json"), encode(campaign()))
        with self.assertRaisesRegex(archive.ArchiveError, "[Dd]eflate"):
            archive.audit_zip(path)

    def test_json_schema_duplicate_keys_and_provider_mismatch_are_rejected(self):
        invalid = [b"{}", b'{"schemaVersion":1,"schemaVersion":1}', encode(dict(campaign(), requestSha256=float('nan')))]
        for raw in invalid:
            with self.subTest(raw=raw[:30]), self.assertRaises(archive.ArchiveError):
                archive.audit_zip(self.zip([("campaign.json", raw)]))
        for provider in ["Npgsql", "PostgreSql"]:
            archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
                ("runs/postgresql/x/attempt-00/result.json", encode(result(provider)))]))
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
                ("runs/sqlite/x/attempt-00/result.json", encode(result("Npgsql")))]))

    def test_heavy_seed_and_jsonl_measurement_roles(self):
        seed = {"task": "DB-Perf-01", "workloadId": "wide8192", "workloadSha256": "a" * 64, "files": {}}
        lines = b'{"utc":"2026-11-24T00:00:00Z","monotonicNs":10}\n\n'
        observed = archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
            ("seeds/wide8192/sqlite/seed.json", encode(seed)),
            ("runs/sqlite/heavy-wide8192/attempt-01/client.resources.jsonl", lines)]))
        self.assertEqual(observed["memberCount"], 3)
        with self.assertRaises(archive.ArchiveError):
            archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
                ("runs/sqlite/x/attempt-00/client.resources.jsonl", lines + b"hidden source\n")]))

    def test_compression_ratio_is_bounded(self):
        with self.assertRaisesRegex(archive.ArchiveError, "ratio"):
            archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
                ("runs/postgresql/x/attempt-00/client.stdout.log", b"x" * (4 * 1024 * 1024))]))

    def test_command_echo_and_all_raw_log_bytes_are_preserved(self):
        log = b"+ python3 runner.py --case mixed\r\nDB_PERF_RESULT output=/tmp/result.json\n"
        observed = archive.audit_zip(self.zip([("campaign.json", encode(campaign())),
            ("runs/postgresql/x/attempt-00/client.stdout.log", log)]))
        item = next(x for x in observed["members"] if x["path"].endswith(".log"))
        self.assertEqual((item["sha256"], item["bytes"]), (sha(log), len(log)))

    def test_capsule_reconstructs_original_zip_and_distinguishes_counts(self):
        value, inventory = self.capsule()
        checked = self.verify(value)
        self.assertEqual(checked["storageChunkCount"], len(value["chunks"]))
        self.assertEqual(checked["logicalMemberCount"], inventory["memberCount"])
        self.assertNotEqual(checked["storageChunkCount"], checked["logicalMemberCount"])
        self.assertFalse(checked["sourcePayloadArchived"])
        self.assertFalse(checked["measurementClaimsVerified"])
        self.assertEqual(archive.verify_capsule(self.root, "checkpoint"), checked)

    def test_missing_reordered_overlapping_and_duplicate_chunks_are_rejected(self):
        value, _ = self.capsule()
        for kind in ["missing", "reordered", "overlap", "duplicate"]:
            changed = copy.deepcopy(value)
            if kind == "missing": changed["chunks"].pop()
            if kind == "reordered": changed["chunks"][0], changed["chunks"][1] = changed["chunks"][1], changed["chunks"][0]
            if kind == "overlap": changed["chunks"][1]["offset"] -= 1
            if kind == "duplicate": changed["chunks"][1]["record"] = changed["chunks"][0]["record"]
            with self.subTest(kind=kind), self.assertRaises(archive.ArchiveError):
                self.verify(changed)

    def test_changed_chunk_byte_with_self_consistent_chunk_hash_is_rejected(self):
        value, _ = self.capsule()
        item = value["chunks"][0]
        raw = bytearray(gzip.decompress((self.root / item["record"]["path"]).read_bytes()))
        raw[20] ^= 1
        item["record"] = self.record(item["record"]["origin"], bytes(raw))
        with self.assertRaisesRegex(archive.ArchiveError, "ZIP identity"):
            self.verify(value)

    def test_inventory_missing_member_forged_role_and_boolean_alias_are_rejected(self):
        value, inventory = self.capsule()
        for kind in ["missing", "role", "boolean"]:
            changed = copy.deepcopy(inventory)
            if kind == "missing": changed["members"].pop(); changed["memberCount"] -= 1
            if kind == "role": changed["members"][0]["role"] = "trusted-source"
            if kind == "boolean": changed["schemaVersion"] = True
            value["inventory"] = self.record("capture/member-inventory.json", encode(changed))
            with self.subTest(kind=kind), self.assertRaises(archive.ArchiveError):
                self.verify(value)

    def test_capsule_artifact_name_and_local_paths_are_bound(self):
        value, _ = self.capsule()
        changed = copy.deepcopy(value); changed["artifact"]["name"] = "db-perf-evidence-999-checkpoint-0"
        with self.assertRaises(archive.ArchiveError): self.verify(changed)
        changed = copy.deepcopy(value); changed["archive"]["filename"] = "checkpoint-1.zip"
        with self.assertRaises(archive.ArchiveError): self.verify(changed)
        changed = copy.deepcopy(value); changed["chunks"][0]["record"]["origin"] = "../outside"
        with self.assertRaises(archive.ArchiveError): self.verify(changed)
        linked = self.root / value["chunks"][0]["record"]["path"]
        backing = self.root / "backing.gz"; linked.replace(backing); linked.symlink_to(backing)
        with self.assertRaises(archive.ArchiveError): self.verify(value)

    def test_gzip_opaque_header_and_trailing_data_are_rejected(self):
        value, _ = self.capsule()
        item = value["chunks"][0]
        raw = gzip.decompress((self.root / item["record"]["path"]).read_bytes())
        original = gzip.compress(raw, mtime=0)
        named = bytearray(original); named[3] = 8
        named = bytes(named[:10]) + b"hidden-source.py\x00" + bytes(named[10:])
        for compressed in [named, original + b"hidden", original + gzip.compress(b"extra")]:
            item["record"] = self.record(item["record"]["origin"], raw, compressed)
            with self.subTest(length=len(compressed)), self.assertRaises(archive.ArchiveError):
                self.verify(value)

    def test_cli_audit_and_capsule_verify_are_reproducible(self):
        value, _ = self.capsule(); self.verify(value)
        script = Path(archive.__file__)
        completed = subprocess.run([sys.executable, str(script), "verify", str(self.root), "checkpoint"],
                                   capture_output=True, text=True, timeout=15)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertEqual(json.loads(completed.stdout)["logicalMemberCount"], 3)
        path = self.zip(); output = self.root / "audit.json"
        completed = subprocess.run([sys.executable, str(script), "audit", str(path), "--output", str(output)],
                                   capture_output=True, text=True, timeout=15)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertEqual(json.loads(output.read_bytes()), archive.audit_zip(path))


if __name__ == "__main__":
    unittest.main()
