"""Read original GitHub ZIPs and lossless indexes without extracting payloads."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import io
from pathlib import Path, PurePosixPath
import subprocess
import tarfile
import zipfile

from strict_io import EvidenceError, Resolver, need, natural, sha_file, strict_json, valid_commit, valid_sha


def safe_name(name):
    need(isinstance(name, str) and name and not PurePosixPath(name).is_absolute()
         and ".." not in PurePosixPath(name).parts and "\\" not in name,
         "archive_path", "Archive has an absolute or traversing member")
    return name.rstrip("/")


class EvidenceZip:
    def __init__(self, path, expected_sha256):
        self.path = Path(path).resolve(strict=True)
        self.sha256 = sha_file(self.path)
        need(self.sha256 == valid_sha(expected_sha256), "archive_hash_mismatch", "Original artifact ZIP bytes do not match the pinned digest")
        self.archive = zipfile.ZipFile(self.path)
        self.entries = {}
        for entry in self.archive.infolist():
            name = safe_name(entry.filename)
            need(name not in self.entries, "duplicate_archive_member", "ZIP repeats a member")
            self.entries[name] = entry
        self.identities = {}

    def read(self, name):
        name = safe_name(name)
        need(name in self.entries and not self.entries[name].is_dir(), "missing_archive_member", "Missing ZIP member: " + name)
        entry = self.entries[name]
        need(entry.file_size <= 256 * 1024 * 1024, "archive_member_too_large", "Raw member exceeds bounded reader")
        try:
            data = self.archive.read(entry)
        except (zipfile.BadZipFile, EOFError) as error:
            raise EvidenceError("archive_member_corrupt", str(error)) from error
        need(len(data) == entry.file_size, "archive_member_length", "ZIP member length differs")
        identity = {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
        if name in self.identities:
            need(self.identities[name] == identity, "archive_member_changed", "Repeated read yielded different bytes")
        self.identities[name] = identity
        return data

    def json(self, name):
        return strict_json(self.read(name))

    def jsonl(self, name):
        return [strict_json(line) for line in self.read(name).splitlines() if line]

    def verify_all(self):
        for name, entry in self.entries.items():
            if not entry.is_dir():
                self.read(name)
        return {"zip_sha256": self.sha256, "zip_bytes": self.path.stat().st_size,
                "member_count": len(self.identities), "decoded_bytes": sum(v["bytes"] for v in self.identities.values())}

    def require_member_hash(self, name, expected):
        data = self.read(name)
        need(hashlib.sha256(data).hexdigest() == valid_sha(expected), "member_reference_hash_mismatch", "Referenced raw bytes differ: " + name)
        return data


class LosslessIndex:
    def __init__(self, plan_root, index_path):
        self.root = Path(plan_root).resolve(strict=True)
        self.index_path = Path(index_path).resolve(strict=True)
        need(self.index_path.is_relative_to(self.root), "index_outside_root", "Lossless index must be inside its declared root")
        self.index = strict_json(self.index_path.read_bytes())
        need(self.index.get("task") == "DB-Perf-01" and self.index.get("kind") == "lossless_diagnostic_checkpoint",
             "wrong_lossless_index", "Expected an explicit lossless DB-Perf checkpoint")
        self.resolver = Resolver(self.root)
        self.records = {item["origin"]: item for item in self.index["files"] if "origin" in item}
        need(len(self.records) == sum("origin" in item for item in self.index["files"]), "duplicate_lossless_origin", "Lossless index repeats an origin")
        self.verified = {}

    def read(self, origin):
        need(origin in self.records, "missing_lossless_origin", "Missing retained original: " + origin)
        item = self.records[origin]
        need(item.get("representation") == "gzip", "unknown_lossless_representation", "Raw original needs its gzip identity")
        resolved = self.resolver.resolve({"path": item["path"], "sha256": item["sha256"], "bytes": item["bytes"],
            "encoding": "gzip", "decoded_sha256": item["original_sha256"], "decoded_bytes": item["original_bytes"]})
        if "original_newline_count" in item:
            need(resolved.data.count(b"\n") == item["original_newline_count"], "original_newline_mismatch", "Lossless raw newline count differs")
        self.verified[origin] = {"stored_sha256": resolved.stored_sha256, "original_sha256": resolved.content_sha256,
                                 "original_bytes": resolved.content_bytes}
        return resolved.data

    def by_hash(self, digest):
        candidates = [name for name, item in self.records.items() if item["original_sha256"] == valid_sha(digest)]
        need(len(candidates) == 1, "ambiguous_lossless_identity", "Expected one retained original for its exact hash")
        return self.read(candidates[0])


def git_bytes(repository, commit, path):
    valid_commit(commit)
    safe_name(path)
    process = subprocess.run(["git", "show", commit + ":" + path], cwd=repository,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    need(process.returncode == 0, "source_object_unavailable", "Pinned public source object is unavailable: " + path, unproven=True)
    return process.stdout


def verified_public_sources(repository, commit, names):
    return {name: {"commit": commit, "sha256": hashlib.sha256(git_bytes(repository, commit, name)).hexdigest()}
            for name in names}


class RuntimeBundle:
    """Stream an original runtime artifact; retain metadata, hashes and ELF headers."""
    def __init__(self, path, expected_sha256):
        self.path = Path(path).resolve(strict=True)
        self.sha256 = sha_file(self.path)
        need(self.sha256 == valid_sha(expected_sha256), "runtime_archive_hash_mismatch", "Runtime ZIP differs from its original API digest")
        self.files, self.small = {}, {}
        with zipfile.ZipFile(self.path) as archive:
            members = archive.namelist()
            need(len(members) == len(set(members)) and set(members) == {"runtime-bundle.tar.gz", "preparation.json"},
                 "runtime_zip_members", "Unexpected runtime artifact members")
            self.preparation = strict_json(archive.read("preparation.json"))
            compressed_sha = hashlib.sha256()
            # ZipExtFile is streamed; no runtime payload is extracted or copied.
            with archive.open("runtime-bundle.tar.gz") as source:
                class HashingReader:
                    def read(self, size=-1):
                        block = source.read(size)
                        compressed_sha.update(block)
                        return block
                with tarfile.open(fileobj=HashingReader(), mode="r|gz") as tar:
                    for item in tar:
                        full = safe_name(item.name)
                        if item.isdir():
                            continue
                        name = full.removeprefix("bundle/")
                        need(name not in self.files, "duplicate_runtime_member", "Runtime tar repeats a member")
                        if item.issym():
                            self.files[name] = {"bytes": item.size, "mode": item.mode, "symlink": item.linkname, "sha256": None}
                            continue
                        need(item.isfile(), "unsupported_runtime_member", "Runtime payload has an unsupported member type")
                        digest, size, header, saved = hashlib.sha256(), 0, bytearray(), bytearray()
                        keep = item.size <= 4 * 1024 * 1024 and (name.endswith((".json", ".cs", ".cpp", ".py", ".csproj", ".props", ".targets", ".h", ".nupkg")))
                        stream = tar.extractfile(item)
                        for block in iter(lambda: stream.read(1024 * 1024), b""):
                            digest.update(block); size += len(block)
                            if len(header) < 64:
                                header.extend(block[:64 - len(header)])
                            if keep:
                                saved.extend(block)
                        need(size == item.size, "runtime_member_length", "Runtime tar member length differs")
                        self.files[name] = {"bytes": size, "mode": item.mode, "symlink": None, "sha256": digest.hexdigest(), "header": bytes(header).hex()}
                        if keep:
                            self.small[name] = bytes(saved)
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    compressed_sha.update(block)
            self.tar_gzip_sha256 = compressed_sha.hexdigest()
        need("bundle.json" in self.small, "missing_bundle_manifest", "Runtime bundle manifest is absent")
        self.manifest = strict_json(self.small["bundle.json"])
        self.manifest_sha256 = hashlib.sha256(self.small["bundle.json"]).hexdigest()
        declared = self.manifest["files"]
        need(set(self.files) == set(declared) | {"bundle.json"}, "runtime_inventory_mismatch", "Actual runtime tar member inventory differs from its manifest")
        for name, expected in declared.items():
            actual = self.files[name]
            need(actual["symlink"] == expected["symlink"], "runtime_member_metadata", "Runtime link identity differs: " + name)
            if actual["symlink"] is None:
                need(actual["mode"] == expected["mode"] and actual["sha256"] == expected["sha256"] and actual["bytes"] == expected["bytes"],
                     "runtime_member_hash", "Actual runtime payload differs: " + name)
            else:
                target = PurePosixPath(name).parent / actual["symlink"]
                # Resolve within the virtual archive, never against the host.
                parts = []
                for part in target.parts:
                    if part == "..":
                        need(bool(parts), "runtime_link_escape", "Runtime symlink escapes the bundle")
                        parts.pop()
                    elif part not in (".", ""):
                        parts.append(part)
                target_name = "/".join(parts)
                need(target_name in self.files and self.files[target_name]["symlink"] is None,
                     "unresolved_runtime_link", "Runtime symlink target is absent or another unresolved link")
                # Producer manifest uses stat(), so mode/length/hash describe
                # the linked regular target. Tar retains the link's own 0777.
                need(self.files[target_name]["sha256"] == expected["sha256"] and self.files[target_name]["bytes"] == expected["bytes"]
                     and self.files[target_name]["mode"] == expected["mode"],
                     "runtime_link_content", "Runtime linked content differs")
        self.summary = {"zip_sha256": self.sha256, "zip_bytes": self.path.stat().st_size,
            "tar_gzip_sha256": self.tar_gzip_sha256, "bundle_manifest_sha256": self.manifest_sha256,
            "files_verified": len(declared), "payload_bytes": sum(v["bytes"] for v in self.files.values()),
            "payload_extracted": False}

    def bytes(self, name):
        need(name in self.small, "runtime_metadata_unavailable", "Requested small runtime metadata is unavailable: " + name)
        return self.small[name]

    def json(self, name):
        return strict_json(self.bytes(name))

    def require_elf(self, name, expected):
        need(name in self.files and self.files[name]["sha256"] == valid_sha(expected), "runtime_executable_mismatch", "Raw executable/native hash differs from actual runtime payload")
        header = bytes.fromhex(self.files[name].get("header", ""))
        need(len(header) >= 20 and header[:6] == b"\x7fELF\x02\x01" and int.from_bytes(header[18:20], "little") == 62,
             "runtime_not_linux_x64_elf", "Actual measured binary is not a Linux x64 ELF")
        return self.files[name]["sha256"]
