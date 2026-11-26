"""Strict, bounded, read-only evidence resolution for offline DB-Perf validation."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import zlib


SHA256 = re.compile(r"[0-9a-f]{64}\Z")
COMMIT = re.compile(r"[0-9a-f]{40}\Z")
MAX_JSON_BYTES = 128 * 1024 * 1024


class EvidenceError(ValueError):
    def __init__(self, code, message, *, unproven=False):
        super().__init__(message)
        self.code = code
        self.unproven = unproven


def need(condition, code, message, *, unproven=False):
    if not condition:
        raise EvidenceError(code, message, unproven=unproven)


def valid_sha(value, name="SHA-256"):
    need(isinstance(value, str) and SHA256.fullmatch(value) is not None and value != "0" * 64,
         "invalid_sha256", f"Invalid {name}")
    return value


def valid_commit(value, name="commit"):
    need(isinstance(value, str) and COMMIT.fullmatch(value) is not None and value != "0" * 40,
         "invalid_source_commit", f"Invalid {name}")
    return value


def natural(value, name, minimum=0):
    need(type(value) is int and value >= minimum, "invalid_counter", f"{name} must be an integer >= {minimum}")
    return value


def finite(value, name, minimum=None):
    need(type(value) in (int, float) and math.isfinite(value), "nonfinite_or_nonnumeric", f"{name} must be finite numeric data")
    need(minimum is None or value >= minimum, "negative_measurement", f"{name} is below its minimum")
    return value


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        need(key not in result, "duplicate_json_key", f"Duplicate JSON key {key}")
        result[key] = value
    return result


def _float(value):
    number = float(value)
    need(math.isfinite(number), "nonfinite_json", f"JSON number overflows: {value}")
    return number


def _constant(value):
    raise EvidenceError("nonfinite_json", f"Nonstandard JSON constant {value}")


def strict_json(data):
    try:
        return json.loads(data, object_pairs_hook=_pairs, parse_float=_float, parse_constant=_constant)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as error:
        raise EvidenceError("invalid_json", str(error)) from error


def sha_file(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


@dataclass(frozen=True)
class Resolved:
    path: Path
    stored_sha256: str
    stored_bytes: int
    content_sha256: str
    content_bytes: int
    encoding: str
    data: bytes


class Resolver:
    def __init__(self, root, max_json_bytes=MAX_JSON_BYTES, named_roots=None):
        self.root = Path(root).resolve(strict=True)
        need(self.root.is_dir(), "invalid_root", "Evidence root must be an existing directory")
        self.roots = {"default": self.root}
        for name, value in (named_roots or {}).items():
            need(isinstance(name, str) and re.fullmatch(r"[a-z][a-z0-9_-]*", name) is not None and name != "default",
                 "invalid_root_name", "Named evidence roots need an explicit nondefault stable name")
            path = Path(value).resolve(strict=True)
            need(path.is_dir(), "invalid_root", "Named evidence root must exist")
            self.roots[name] = path
        self.max_json_bytes = natural(max_json_bytes, "maximum evidence bytes", 1)

    def file(self, reference, *, elf=False):
        need(isinstance(reference, dict), "missing_reference", "A typed file reference is required", unproven=True)
        allowed = {"path", "sha256", "bytes", "encoding", "decoded_sha256", "decoded_bytes", "root"}
        need(set(reference) <= allowed and {"path", "sha256"} <= reference.keys(), "invalid_reference", "Unknown or missing reference fields")
        root_name = reference.get("root", "default")
        need(isinstance(root_name, str) and root_name in self.roots, "unregistered_root", "Evidence cannot register or invent another root directory")
        root = self.roots[root_name]
        name = reference["path"]
        need(isinstance(name, str) and name and not Path(name).is_absolute() and ".." not in Path(name).parts,
             "outside_reference", "Evidence path must stay within the declared evidence root")
        try:
            path = (root / name).resolve(strict=True)
        except (FileNotFoundError, OSError) as error:
            raise EvidenceError("missing_file", f"Missing evidence: {name}", unproven=True) from error
        need(path.is_relative_to(root) and path.is_file(), "outside_reference", "Evidence path escapes its root or is not a file")
        expected = valid_sha(reference["sha256"])
        size = path.stat().st_size
        need(sha_file(path) == expected, "stored_hash_mismatch", f"Stored bytes differ: {name}")
        if "bytes" in reference:
            need(size == natural(reference["bytes"], "stored byte count"), "stored_length_mismatch", f"Stored length differs: {name}")
        if elf:
            need(reference.get("encoding", "identity") == "identity", "elf_compressed", "ELF identity must refer to its actual unpacked bytes")
            with path.open("rb") as stream:
                header = stream.read(64)
            need(len(header) == 64 and header[:6] == b"\x7fELF\x02\x01" and
                 struct.unpack_from("<H", header, 18)[0] == 62 and struct.unpack_from("<H", header, 16)[0] in (2, 3),
                 "wrong_elf", "Expected a Linux x64 ELF executable/shared object")
        return path

    def resolve(self, reference):
        path = self.file(reference)
        encoding = reference.get("encoding", "identity")
        need(encoding in ("identity", "gzip"), "unknown_encoding", "Only identity and explicitly bound gzip evidence are supported")
        stored_size = path.stat().st_size
        need(stored_size <= self.max_json_bytes, "evidence_too_large", "Stored evidence exceeds the bounded JSON reader")
        stored = path.read_bytes()
        need(hashlib.sha256(stored).hexdigest() == reference["sha256"], "evidence_changed_during_read", "Evidence changed after its first hash check")
        if encoding == "identity":
            need("decoded_sha256" not in reference and "decoded_bytes" not in reference,
                 "ambiguous_identity_reference", "Uncompressed references do not have a second content identity")
            data = stored
        else:
            expected_size = natural(reference.get("decoded_bytes"), "decoded byte count")
            need(expected_size <= self.max_json_bytes, "decoded_evidence_too_large", "Decoded bytes exceed the bounded JSON reader")
            expected_sha = valid_sha(reference.get("decoded_sha256"), "decoded SHA-256")
            try:
                inflater = zlib.decompressobj(16 + zlib.MAX_WBITS)
                data = inflater.decompress(stored, expected_size + 1)
                need(inflater.eof and not inflater.unconsumed_tail and not inflater.unused_data,
                     "gzip_framing", "Gzip must contain one complete member without trailing bytes")
            except zlib.error as error:
                raise EvidenceError("invalid_gzip", str(error)) from error
            need(len(data) == expected_size, "decoded_length_mismatch", "Decoded raw byte count differs")
            need(hashlib.sha256(data).hexdigest() == expected_sha, "decoded_hash_mismatch", "Decoded raw bytes differ from the original identity")
        return Resolved(path, reference["sha256"], len(stored), hashlib.sha256(data).hexdigest(), len(data), encoding, data)

    def json(self, reference):
        resolved = self.resolve(reference)
        return strict_json(resolved.data), resolved


def field(value, *names, optional=False, default=None):
    """Accept the producers' camel/Pascal/snake spelling, never ambiguous aliases."""
    need(isinstance(value, dict), "invalid_object", "Expected a JSON object")
    canonical = lambda name: name.replace("_", "").lower()
    wanted = {canonical(name) for name in names}
    matches = [name for name in value if canonical(name) in wanted]
    need(len(matches) <= 1, "ambiguous_field", f"Ambiguous producer aliases for {names[0]}")
    if not matches:
        need(optional, "missing_raw_field", f"Missing raw field {names[0]}", unproven=True)
        return default
    return value[matches[0]]
