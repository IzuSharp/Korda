#!/usr/bin/env python3
"""Offline, stdlib-only verification of DB-Perf-01 measurement artifacts.

This verifies byte preservation and measurement-file roles, not business counters,
GitHub authenticity, performance acceptance, or a completed long-test campaign.
No archive member is extracted or executed. Source/build/runtime archives belong
in Korda or their original artifact store, never inside this measurement capsule.

``audit_zip(path, expected_sha256=None, expected_members=None)`` returns a
deterministic member inventory. Expected members may contain just path/bytes/SHA;
any additional inventory fields supplied are also compared.

``verify_capsule(plan_root, verification_path_or_checkpoint_directory)`` reads
``read-only-verification.json.measurementArtifactArchive``. Its version-1 shape is:

  {schemaVersion: 1, kind: "measurement-artifact-byte-chunks",
   artifact: {repository: "IzuSharp/Korda", runId, runAttempt, jobId, artifactId,
              name: "db-perf-evidence-<runId>-checkpoint-<0..8>"},
   archive: {filename: "checkpoint-<0..8>.zip", bytes, sha256},
   chunks: [{index, offset, length, record: <schema-3 gzip raw binding>}],
   inventory: <schema-3 gzip raw binding of the complete audit_zip JSON>}

The gzip binding has origin/path/representation/sha256/bytes/original_sha256/
original_bytes/original_newline_count. Paths are relative to Plan, and bind to
<checkpoint>/raw/<origin>.gz. Chunk count never means business-record count.
"""
from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import tempfile
from typing import Any
import zipfile
import zlib


MAX_ARCHIVE_BYTES = 512 * 1024 * 1024
MAX_MEMBER_BYTES = 128 * 1024 * 1024
MAX_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_MEMBERS = 20000
MAX_RATIO = 1000
MAX_LINE_BYTES = 8 * 1024 * 1024
MAX_STRING_BYTES = 8 * 1024 * 1024
MAX_CHUNK_BYTES = 2 * 1024 * 1024
MAX_CHUNKS = 4096
MAX_INVENTORY_BYTES = 16 * 1024 * 1024
MAX_DEPTH = 96
PROVIDERS = {"postgresql", "sqlite"}
HASH = re.compile(r"[0-9a-f]{64}\Z")
SAFE_SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,199}\Z")
FORBIDDEN_SEGMENTS = {"source", "sources", "src", "helper", "helpers", "implementation",
                      "runtime", "bundle", "apps", "bin", "obj", "feed", "packages"}
ROOT_ROLES = {"campaign.json": "campaign", "environment.json": "environment",
              "quota-observation.json": "quota", "runtime-dependencies.json": "dependencies"}
RUN_ROLES = {"client.command.json": "command", "client.resources.jsonl": "resources",
             "client.stdout.log": "stdout", "result.json": "result",
             "result.json.oracle-failure.json": "oracle-failure",
             "database-retirement.json": "retirement", "normalized.json": "normalized",
             "plan.json": "plan", "runner.json": "runner",
             "query-plan-evidence.json": "query-plan", "postgres-owner.json": "postgres-owner",
             "postgres-settings.txt": "postgres-settings", "postgres.log": "postgres-log",
             "postgres-init.log": "postgres-init", "postgres-query-plans.json": "postgres-jsonlog",
             "postgres-query-plans.log": "postgres-log"}
SEED_ROLES = {"initialize.command.json": "command", "initialize.resources.jsonl": "resources",
              "initialize.stdout.log": "stdout", "seed.json": "seed",
              "postgres-owner.json": "postgres-owner", "postgres-settings.txt": "postgres-settings",
              "postgres.log": "postgres-log", "postgres-init.log": "postgres-init"}
REQUIRED = {
    "campaign": {"schemaVersion", "task", "state", "requestSha256", "bundleManifestSha256", "completedSteps"},
    "environment": {"schemaVersion", "identity", "limits", "qualified"},
    "quota": {"schemaVersion", "task", "kind", "identity", "limits", "qualified"},
    "dependencies": {"schemaVersion", "images", "libraries", "scope"},
    "command": {"name", "phase", "argv", "startedUtc", "startedMonotonicNs"},
    "resources": {"utc", "monotonicNs"},
    "result": {"schemaVersion", "route", "provider", "executableSha256"},
    "oracle-failure": {"schema", "oracleFailures", "firstOracleFailure", "scope"},
    "retirement": {"schemaVersion", "retiredUtc", "rawResultSha256", "seedReceiptSha256", "files"},
    "normalized": {"provider", "route", "name", "measurement", "rawPath", "rawSha256", "evidenceFiles"},
    "plan": {"provider", "connection", "commands"},
    "runner": {"requestSha256", "bundleManifestSha256", "environmentIdentity", "controllerSources"},
    "query-plan": {"schemaVersion", "task", "diagnostic", "provider", "route", "passed", "scope"},
    "postgres-owner": {"argv", "startedUtc", "startedMonotonicNs"},
    "postgres-jsonlog": {"timestamp", "pid", "message"},
    "seed": {"task", "workloadId", "workloadSha256", "files"},
}
SOURCE_TEXT = re.compile(
    r"(?m)^\s*(?:#!\s*/[^\n]*(?:python|bash|/sh)\b|#\s*include\s*[<\"]|"
    r"using\s+(?:System(?:\.[\w.]+)?|[A-Z]\w*(?:\.\w+)+)\s*;|"
    r"namespace\s+[\w.]+\s*[;{]|"
    r"(?:public|internal|private|protected)\s+(?:(?:sealed|static|partial|abstract)\s+)*"
    r"(?:class|struct|record|interface|enum)\s+\w+|"
    r"(?:async\s+)?def\s+\w+\s*\(|class\s+\w+\s*[:(]|"
    r"import\s+(?:os|sys|pathlib|subprocess|json|argparse|hashlib|zipfile|base64|ctypes)\b|"
    r"from\s+[\w.]+\s+import\s+|(?:export\s+)?(?:async\s+)?function\s+\w+\s*\(|"
    r"<Project(?:\s|>)|(?:int|void)\s+main\s*\(|print\s*\(|console\.log\s*\()")
PAYLOAD_KEYS = {"sourcecode", "sourcetext", "sourcebytes", "implementationtext", "scripttext",
                "scriptcontents", "filecontent", "filecontents", "payloadbase64", "base64",
                "encodedpayload", "binarypayload", "embeddedsource"}
BASE64_TOKEN = re.compile(r"(?<![A-Za-z0-9_+/=-])[A-Za-z0-9_+/-]{16,}={0,2}(?![A-Za-z0-9_+/=-])")
GZIP_FIELDS = {"origin", "path", "representation", "sha256", "bytes", "original_sha256",
               "original_bytes", "original_newline_count"}


class ArchiveError(ValueError):
    """An archive or capsule is outside the supported measurement-only format."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ArchiveError(message)


def integer(value: Any, name: str, minimum: int = 0, maximum: int | None = None) -> int:
    require(type(value) is int and value >= minimum and (maximum is None or value <= maximum),
            "Invalid " + name)
    return value


def digest(value: Any, name: str = "SHA-256") -> str:
    require(isinstance(value, str) and HASH.fullmatch(value) is not None, "Invalid " + name)
    return value


def sha_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def same_json(first: Any, second: Any) -> bool:
    # Python equality alone treats True, 1 and 1.0 as equal.
    encode = lambda value: json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    return encode(first) == encode(second)


def pairs(items):
    result = {}
    for key, value in items:
        require(key not in result, "Duplicate JSON object key: " + key)
        result[key] = value
    return result


def parse_json(raw: bytes, name: str) -> Any:
    def invalid_constant(value):
        raise ArchiveError("Non-finite JSON constant: " + value)
    try:
        return json.loads(raw.decode("utf-8-sig"), object_pairs_hook=pairs, parse_constant=invalid_constant)
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise ArchiveError("Invalid UTF-8 JSON in " + name) from error


def safe_relative(value: Any) -> PurePosixPath:
    require(isinstance(value, str) and value and len(value) <= 2048 and "\\" not in value,
            "Invalid relative path")
    path = PurePosixPath(value)
    require(not path.is_absolute() and str(path) == value and all(
        part not in {".", ".."} and SAFE_SEGMENT.fullmatch(part) for part in path.parts),
        "Noncanonical or traversing path: " + value)
    return path


def member_role(name: str) -> tuple[str, str, str | None]:
    parts = safe_relative(name).parts
    require(not any(p.casefold() in FORBIDDEN_SEGMENTS for p in parts), "Source/runtime directory in measurement archive")
    if len(parts) == 1:
        role, provider = ROOT_ROLES.get(parts[0]), None
    elif len(parts) == 5 and parts[0] == "runs" and parts[1] in PROVIDERS:
        require(re.fullmatch(r"attempt-[0-9]{2,6}", parts[3]) is not None, "Invalid measurement attempt directory")
        role, provider = RUN_ROLES.get(parts[4]), parts[1]
    elif parts[0] == "seeds" and len(parts) in {3, 4} and parts[-2] in PROVIDERS:
        require(len(parts) == 3 or parts[1] not in PROVIDERS, "Invalid heavy seed layout")
        role, provider = SEED_ROLES.get(parts[-1]), parts[-2]
    else:
        role, provider = None, None
    require(role is not None, "Unrecognized measurement file role: " + name)
    require(not role.startswith("postgres-") or provider == "postgresql", "PostgreSQL file in another provider directory")
    form = "jsonl" if role in {"resources", "postgres-jsonlog"} else "json" if role in REQUIRED else "text"
    return role, form, provider


def binary_payload(raw: bytes) -> bool:
    signatures = (b"\x7fELF", b"MZ", b"PK\x03\x04", b"PK\x05\x06", b"PK\x07\x08", b"\x1f\x8b",
                  b"BZh", b"\xfd7zXZ\x00", b"7z\xbc\xaf\x27\x1c", b"Rar!", b"\x28\xb5\x2f\xfd",
                  b"SQLite format 3\x00", b"!<arch>\n", b"\xfe\xed\xfa\xce", b"\xcf\xfa\xed\xfe")
    return raw.startswith(signatures) or (len(raw) >= 262 and raw[257:262] == b"ustar")


def scan_string(value: str, name: str) -> None:
    try:
        raw = value.encode("utf-8")
    except UnicodeError as error:
        raise ArchiveError("Invalid Unicode string in " + name) from error
    require(len(raw) <= MAX_STRING_BYTES and "\x00" not in value, "Oversized or binary string in " + name)
    require(not binary_payload(raw) and not SOURCE_TEXT.search(value), "Implementation or binary payload in " + name)
    for match in BASE64_TOKEN.finditer(value):
        token = match[0]
        if re.fullmatch(r"[0-9a-fA-F]{40}|[0-9a-fA-F]{64}", token):
            continue  # Ordinary Git/SHA identities, not an encoded content field.
        decoded = []
        if len(token) % 2 == 0 and re.fullmatch(r"[0-9a-fA-F]+", token):
            decoded.append(bytes.fromhex(token))
        try:
            decoded.append(base64.b64decode(token + "=" * (-len(token) % 4), altchars=b"-_", validate=True))
        except (ValueError, binascii.Error):
            pass
        for data in decoded:
            require(not binary_payload(data), "Encoded nested archive/binary payload in " + name)
            try:
                text = data.decode("utf-8")
            except UnicodeError:
                continue
            require(not SOURCE_TEXT.search(text), "Encoded implementation payload in " + name)


def scan_value(value: Any, name: str, depth: int = 0) -> None:
    require(depth <= MAX_DEPTH, "JSON nesting exceeds bound in " + name)
    if isinstance(value, dict):
        for key, item in value.items():
            scan_string(key, name + "/<key>")
            compact = re.sub(r"[^a-z]", "", key.casefold())
            require(compact not in PAYLOAD_KEYS or not isinstance(item, (str, list, dict)),
                    "Embedded content field in " + name + ": " + key)
            scan_value(item, name + "/" + key, depth + 1)
    elif isinstance(value, list):
        for item in value:
            scan_value(item, name, depth + 1)
    elif isinstance(value, str):
        scan_string(value, name)


def validate_document(value: Any, role: str, name: str, provider: str | None) -> None:
    require(isinstance(value, dict), "Measurement JSON must be an object: " + name)
    folded = {key.casefold(): item for key, item in value.items()}
    require(len(folded) == len(value), "Ambiguous case-folded JSON keys: " + name)
    require({k.casefold() for k in REQUIRED[role]} <= folded.keys(), "Missing " + role + " schema fields: " + name)
    if "task" in folded:
        require(folded["task"] == "DB-Perf-01", "Foreign task in measurement file")
    if "schemaversion" in folded:
        integer(folded["schemaversion"], "measurement schemaVersion", 1, 100)
    if "provider" in folded and provider:
        names = {provider}
        if role == "result":
            names.add("npgsql" if provider == "postgresql" else "microsoft.data.sqlite.core")
        require(str(folded["provider"]).casefold() in names, "Provider differs from member path: " + name)
    if "argv" in folded:
        require(isinstance(folded["argv"], list) and 0 < len(folded["argv"]) <= 4096
                and all(isinstance(x, str) for x in folded["argv"]), "Invalid command argv")
    if role == "result":
        digest(folded["executablesha256"], "result executable SHA")
        require("completed" in folded or "phases" in folded or "checks" in folded,
                "Result lacks measured operations or diagnostic outcomes")
    if role == "resources":
        integer(folded["monotonicns"], "resource monotonic time")
    if role == "postgres-jsonlog":
        require(isinstance(folded["message"], str), "PostgreSQL message is not text")
    if role == "oracle-failure":
        require(folded["schema"] == "DB-Perf-01.oracle-failure.v1", "Unknown oracle failure schema")
    scan_value(value, name)


def verify_compressed_range(stream, start: int, info: zipfile.ZipInfo) -> None:
    """ZipExtFile can ignore bytes after deflate EOF inside compress_size."""
    if info.compress_type == zipfile.ZIP_STORED:
        require(info.compress_size == info.file_size, "Stored ZIP member has hidden extra bytes")
        return
    require(info.compress_type == zipfile.ZIP_DEFLATED, "Unsupported ZIP compression")
    decoder = zlib.decompressobj(-15)
    remaining, expanded, crc = info.compress_size, 0, 0
    stream.seek(start)
    while remaining:
        block = stream.read(min(65536, remaining))
        require(block, "Truncated member compressed range")
        remaining -= len(block)
        while block:
            raw = decoder.decompress(block, MAX_LINE_BYTES)
            expanded += len(raw); crc = zlib.crc32(raw, crc)
            require(expanded <= info.file_size and not decoder.unused_data,
                    "Deflate expanded size or hidden trailing payload differs")
            block = decoder.unconsumed_tail
            require(not decoder.eof or not remaining and not block, "Bytes follow deflate EOF inside ZIP member")
    require(decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail
            and expanded == info.file_size and crc == info.CRC,
            "Deflate stream length/CRC/EOF differs from original member")


def verify_zip_framing(stream, archive: zipfile.ZipFile, size: int) -> None:
    """No executable preamble, arbitrary extra fields, comments, gaps or trailer.

    Current Actions measurement artifacts are classic single-disk stored/deflate
    ZIPs. Explicit bounds keep ZIP64 unnecessary; unsupported framing fails closed.
    """
    require(size >= 22 and not archive.comment, "ZIP comments or truncated archive are unsupported")
    stream.seek(size - 22)
    end = struct.unpack("<4s4H2IH", stream.read(22))
    signature, disk, central_disk, disk_count, count, central_size, central_offset, comment_size = end
    require(signature == b"PK\x05\x06" and disk == central_disk == comment_size == 0
            and disk_count == count == len(archive.infolist()) and central_offset == archive.start_dir
            and central_offset + central_size == size - 22,
            "Unsupported ZIP64/multidisk/comment/trailing framing")
    cursor = 0
    ordered = sorted(archive.infolist(), key=lambda info: info.header_offset)
    for index, info in enumerate(ordered):
        require(info.header_offset == cursor and not info.extra and not info.comment,
                "ZIP gap/preamble/extra/comment can conceal unindexed data")
        stream.seek(cursor)
        header = stream.read(30)
        require(len(header) == 30, "Truncated local ZIP header")
        sig, version, flags, method, _, _, crc, compressed, expanded, name_len, extra_len = struct.unpack("<4s5H3I2H", header)
        require(sig == b"PK\x03\x04" and version <= 20 and flags == info.flag_bits and method == info.compress_type
                and extra_len == 0, "Local ZIP framing differs or has opaque extra data")
        raw_name = stream.read(name_len)
        try:
            name = raw_name.decode("utf-8" if flags & 0x800 else "cp437")
        except UnicodeError as error:
            raise ArchiveError("Invalid local ZIP filename") from error
        require(name == info.filename == info.orig_filename, "Local/central ZIP names differ")
        verify_compressed_range(stream, cursor + 30 + name_len, info)
        cursor += 30 + name_len + info.compress_size
        boundary = ordered[index+1].header_offset if index+1 < len(ordered) else central_offset
        if flags & 8:
            require(crc in {0, info.CRC} and compressed in {0, info.compress_size} and expanded in {0, info.file_size},
                    "Local data-descriptor sizes differ")
            length = boundary - cursor
            require(length in {12, 16}, "Unexpected ZIP descriptor or unindexed payload")
            stream.seek(cursor)
            descriptor = stream.read(length)
            if length == 16:
                require(descriptor[:4] == b"PK\x07\x08", "Invalid ZIP data-descriptor signature")
                descriptor = descriptor[4:]
            require(struct.unpack("<3I", descriptor) == (info.CRC, info.compress_size, info.file_size),
                    "ZIP descriptor does not match member")
            cursor += length
        else:
            require((crc, compressed, expanded) == (info.CRC, info.compress_size, info.file_size), "ZIP local sizes differ")
        require(cursor == boundary, "Unindexed bytes between ZIP records")
    require(cursor == central_offset, "Unindexed bytes before ZIP directory")


def audit_zip(zip_path, expected_sha256=None, expected_members=None) -> dict:
    """Verify every original member without extracting files or executing text."""
    path = Path(zip_path)
    size = integer(path.stat().st_size, "archive bytes", 1, MAX_ARCHIVE_BYTES)
    with path.open("rb") as stream:
        archive_sha = hashlib.file_digest(stream, "sha256").hexdigest()
        if expected_sha256 is not None:
            require(archive_sha == digest(expected_sha256), "Original archive SHA-256 differs")
        stream.seek(0)
        try:
            with zipfile.ZipFile(stream) as archive:
                infos = archive.infolist()
                require(0 < len(infos) <= MAX_MEMBERS, "ZIP member count outside bound")
                # Apply declared size/ratio limits before the framing pass also
                # decompresses each deflate stream to reject hidden trailing data.
                declared_total = 0
                for info in infos:
                    integer(info.file_size, "member bytes", 0, MAX_MEMBER_BYTES)
                    require(info.file_size <= max(info.compress_size, 1) * MAX_RATIO, "ZIP member compression ratio exceeds bound")
                    declared_total += info.file_size
                    require(declared_total <= MAX_TOTAL_BYTES, "Total uncompressed ZIP bytes exceed bound")
                verify_zip_framing(stream, archive, size)
                names = set()
                total = 0
                members = []
                for info in infos:
                    name = info.filename
                    require(name not in names and name == info.orig_filename, "Duplicate or ambiguous ZIP member")
                    names.add(name)
                    role, form, provider = member_role(name)
                    require(not info.is_dir() and stat.S_IFMT(info.external_attr >> 16) in {0, stat.S_IFREG}
                            and not (info.external_attr & 0x10), "ZIP member is not a regular file")
                    require(info.flag_bits & ~(8 | 0x800) == 0, "Encrypted/unsupported ZIP flags")
                    require(info.compress_type in {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}, "Unsupported ZIP compression")
                    integer(info.file_size, "member bytes", 0, MAX_MEMBER_BYTES)
                    require(info.file_size <= max(info.compress_size, 1) * MAX_RATIO, "ZIP member compression ratio exceeds bound")
                    total += info.file_size
                    require(total <= MAX_TOTAL_BYTES, "Total uncompressed ZIP bytes exceed bound")
                    hasher, length, newlines = hashlib.sha256(), 0, 0
                    with archive.open(info) as member:
                        if form == "jsonl":
                            while True:
                                line = member.readline(MAX_LINE_BYTES + 1)
                                if not line:
                                    break
                                require(len(line) <= MAX_LINE_BYTES, "JSONL line exceeds bound")
                                hasher.update(line); length += len(line); newlines += line.count(b"\n")
                                require(length <= info.file_size, "ZIP member expands beyond declared size")
                                if line.strip():
                                    validate_document(parse_json(line, name), role, name, provider)
                        else:
                            raw = member.read(MAX_MEMBER_BYTES + 1)
                            require(len(raw) == info.file_size, "ZIP member length differs")
                            hasher.update(raw); length = len(raw); newlines = raw.count(b"\n")
                            require(not binary_payload(raw), "Nested archive/binary disguised as measurement text")
                            if form == "json":
                                validate_document(parse_json(raw, name), role, name, provider)
                            else:
                                try:
                                    text = raw.decode("utf-8")
                                except UnicodeError as error:
                                    raise ArchiveError("Non-UTF-8 measurement log") from error
                                # Read complete logs unchanged. Shell command echoes
                                # remain data; source files/code blocks are rejected.
                                for line in text.splitlines():
                                    scan_string(line, name)
                    require(length == info.file_size, "Original ZIP member byte count differs")
                    members.append({"path": name, "bytes": length, "sha256": hasher.hexdigest(),
                                    "role": role, "format": form, "newlineCount": newlines})
        except (zipfile.BadZipFile, NotImplementedError, RuntimeError, struct.error, zlib.error) as error:
            raise ArchiveError("Invalid or unsupported measurement ZIP: " + str(error)) from error
    require("campaign.json" in names, "Campaign measurement artifact lacks campaign.json")
    members.sort(key=lambda item: item["path"])
    if expected_members is not None:
        require(isinstance(expected_members, list) and len(expected_members) == len(members), "Expected ZIP member count differs")
        expected = {}
        for item in expected_members:
            require(isinstance(item, dict) and {"path", "bytes", "sha256"} <= item.keys(), "Invalid expected member inventory")
            safe_relative(item["path"]); digest(item["sha256"]); integer(item["bytes"], "expected member length")
            require(item["path"] not in expected, "Duplicate expected ZIP member")
            expected[item["path"]] = item
        require(set(expected) == names, "Expected ZIP member set differs")
        for item in members:
            require(all(key in item and same_json(item[key], value) for key, value in expected[item["path"]].items()),
                    "Expected ZIP member identity or role differs: " + item["path"])
    return {"schemaVersion": 1, "kind": "measurement-artifact-member-audit",
            "archiveSha256": archive_sha, "archiveBytes": size, "memberCount": len(members),
            "uncompressedBytes": total, "members": members,
            "sourcePayloadArchived": False, "performanceAcceptance": False, "measurementClaimsVerified": False,
            "policy": {"name": "DB-Perf-01-measurement-archive-v1", "maxArchiveBytes": MAX_ARCHIVE_BYTES,
                       "maxMemberBytes": MAX_MEMBER_BYTES, "maxUncompressedBytes": MAX_TOTAL_BYTES,
                       "maxMembers": MAX_MEMBERS, "maxCompressionRatio": MAX_RATIO,
                       "zipFraming": "classic single disk; stored/deflate; no extras, comments, gaps, preamble or trailer"},
            "logScope": "Original stdout/PostgreSQL logs and argv are measurement records; textual command echoes are retained, never executed or rewritten. Complete implementation and encoded/nested source/binary payloads are rejected.",
            "payloadRuleScope": "sourcePayloadArchived=false is limited to this exact artifact, allowed measurement roles and the documented v1 text/encoded-payload detection rules. It is not a proof covering every programming language, arbitrary encoding or covert data representation; actual artifact provenance and independent review remain required.",
            "scope": "Byte and measurement-role audit only; source/binary/workload/environment, count ledgers, business correctness and performance acceptance still require their existing independent audits."}


def local_file(root: Path, relative: str) -> Path:
    parts = safe_relative(relative).parts
    target = root
    for part in parts:
        target /= part
        require(not target.is_symlink(), "Symlink in capsule file path")
    require(target.is_file() and target.resolve().is_relative_to(root), "Capsule file missing or outside Plan root")
    return target


def gzip_record(root: Path, checkpoint: str, record: Any, maximum: int) -> bytes:
    require(isinstance(record, dict) and set(record) == GZIP_FIELDS, "Incomplete schema-3 gzip binding")
    require(record["representation"] == "gzip", "Capsule records require original-byte gzip binding")
    origin = str(safe_relative(record["origin"]))
    require(record["path"] == checkpoint + "/raw/" + origin + ".gz", "Capsule record does not bind its origin")
    compressed_bytes = integer(record["bytes"], "gzip bytes", 1, maximum + 65536)
    original_bytes = integer(record["original_bytes"], "original gzip bytes", 1, maximum)
    integer(record["original_newline_count"], "original gzip newline count")
    file = local_file(root, record["path"])
    require(file.stat().st_size == compressed_bytes, "Compressed record length differs")
    compressed = file.read_bytes()
    require(len(compressed) == compressed_bytes and sha_bytes(compressed) == digest(record["sha256"]), "Compressed record identity differs")
    require(len(compressed) >= 18 and compressed[:3] == b"\x1f\x8b\x08" and compressed[3] == 0,
            "Gzip extra/comment/name can conceal unindexed payload")
    try:
        decoder = zlib.decompressobj(31)
        raw = decoder.decompress(compressed, original_bytes + 1)
        require(decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail,
                "Gzip must be one complete stream without trailing payload")
    except zlib.error as error:
        raise ArchiveError("Invalid gzip capsule record") from error
    require(len(raw) == original_bytes and sha_bytes(raw) == digest(record["original_sha256"])
            and raw.count(b"\n") == record["original_newline_count"], "Original record bytes/SHA/newlines differ")
    return raw


def verify_capsule(plan_root, verification_path_or_checkpoint_directory) -> dict:
    """Reconstruct one exact ZIP in temporary storage; verify actual members."""
    root = Path(plan_root).resolve()
    target = Path(verification_path_or_checkpoint_directory)
    target = target if target.is_absolute() else root / target
    if target.is_dir():
        target /= "read-only-verification.json"
    require(target.is_relative_to(root), "Capsule receipt is outside Plan root")
    target = local_file(root, target.relative_to(root).as_posix())
    require(target.name == "read-only-verification.json" and target.stat().st_size <= MAX_INVENTORY_BYTES,
            "Unsupported capsule receipt")
    document = parse_json(target.read_bytes(), str(target))
    capsule = document.get("measurementArtifactArchive") if isinstance(document, dict) else None
    require(isinstance(capsule, dict) and set(capsule) == {"schemaVersion", "kind", "artifact", "archive", "chunks", "inventory"}
            and type(capsule["schemaVersion"]) is int and capsule["schemaVersion"] == 1
            and capsule["kind"] == "measurement-artifact-byte-chunks", "Invalid capsule schema")
    artifact = capsule["artifact"]
    require(isinstance(artifact, dict) and set(artifact) == {"repository", "runId", "runAttempt", "jobId", "artifactId", "name"}
            and artifact["repository"] == "IzuSharp/Korda", "Invalid Actions artifact identity")
    for field in ("runId", "runAttempt", "jobId", "artifactId"):
        integer(artifact[field], "artifact " + field, 1, 2**63 - 1)
    match = re.fullmatch(r"db-perf-evidence-" + str(artifact["runId"]) + r"-checkpoint-([0-8])", str(artifact["name"]))
    require(match is not None, "Artifact name does not bind its run/checkpoint")
    archive = capsule["archive"]
    require(isinstance(archive, dict) and set(archive) == {"filename", "bytes", "sha256"}
            and archive["filename"] == "checkpoint-" + match[1] + ".zip", "Archive filename differs from artifact checkpoint")
    integer(archive["bytes"], "original archive bytes", 1, MAX_ARCHIVE_BYTES); digest(archive["sha256"])
    chunks = capsule["chunks"]
    require(isinstance(chunks, list) and 0 < len(chunks) <= MAX_CHUNKS, "Invalid capsule chunk count")
    checkpoint = target.parent.relative_to(root).as_posix()
    inventory_raw = gzip_record(root, checkpoint, capsule["inventory"], MAX_INVENTORY_BYTES)
    inventory = parse_json(inventory_raw, "capsule member inventory")
    require(isinstance(inventory, dict) and inventory.get("kind") == "measurement-artifact-member-audit"
            and inventory.get("archiveSha256") == archive["sha256"] and inventory.get("archiveBytes") == archive["bytes"],
            "Member inventory belongs to another archive")
    seen = {capsule["inventory"]["path"]}
    offset, hasher = 0, hashlib.sha256()
    with tempfile.TemporaryDirectory(prefix="db-perf-measurement-capsule-") as temporary:
        path = Path(temporary) / archive["filename"]
        with path.open("xb") as stream:
            for index, item in enumerate(chunks):
                require(isinstance(item, dict) and set(item) == {"index", "offset", "length", "record"}, "Invalid chunk entry")
                integer(item["index"], "chunk index"); integer(item["offset"], "chunk offset")
                require(item["index"] == index and item["offset"] == offset, "Missing, reordered or overlapping capsule chunk")
                integer(item["length"], "chunk length", 1, MAX_CHUNK_BYTES)
                raw = gzip_record(root, checkpoint, item["record"], MAX_CHUNK_BYTES)
                require(item["record"]["path"] not in seen, "Duplicate capsule chunk path")
                seen.add(item["record"]["path"])
                require(len(raw) == item["length"], "Chunk length differs from original bytes")
                offset += len(raw)
                require(offset <= archive["bytes"], "Capsule chunks exceed original ZIP length")
                stream.write(raw); hasher.update(raw)
        require(offset == archive["bytes"] and hasher.hexdigest() == archive["sha256"], "Reassembled original ZIP identity differs")
        observed = audit_zip(path, archive["sha256"], inventory.get("members"))
        require(same_json(observed, inventory), "Complete member inventory/audit differs from actual ZIP")
    return {"schemaVersion": 1, "kind": "verified-measurement-artifact-capsule", "artifact": artifact,
            "archive": archive, "storageChunkCount": len(chunks), "logicalMemberCount": observed["memberCount"],
            "uncompressedBytes": observed["uncompressedBytes"], "inventorySha256": sha_bytes(inventory_raw),
            "sourcePayloadArchived": False, "performanceAcceptance": False, "measurementClaimsVerified": False,
            "payloadRuleScope": observed["payloadRuleScope"],
            "scope": "Offline original ZIP/member integrity and role audit. Actions IDs are declared and byte-bound, not remotely authenticated. Storage chunks are not business measurements; existing acceptance checks remain required."}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    audit = commands.add_parser("audit")
    audit.add_argument("zip_path", type=Path)
    audit.add_argument("--expected-sha256")
    audit.add_argument("--expected-members", type=Path)
    audit.add_argument("--output", type=Path)
    verify = commands.add_parser("verify")
    verify.add_argument("plan_root", type=Path)
    verify.add_argument("verification_path", type=Path)
    verify.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        if args.command == "audit":
            expected = parse_json(args.expected_members.read_bytes(), str(args.expected_members)) if args.expected_members else None
            if isinstance(expected, dict):
                require(expected.get("kind") == "measurement-artifact-member-audit", "Unknown expected member inventory")
                result = audit_zip(args.zip_path, args.expected_sha256 or expected.get("archiveSha256"), expected.get("members"))
                require(same_json(result, expected), "Complete expected audit differs")
            else:
                result = audit_zip(args.zip_path, args.expected_sha256, expected)
        else:
            result = verify_capsule(args.plan_root, args.verification_path)
        text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
        if args.output:
            with args.output.open("x", encoding="utf-8") as output:
                output.write(text)
        else:
            print(text, end="")
        return 0
    except (ArchiveError, OSError, ValueError) as error:
        parser.exit(1, "measurement archive rejected: " + str(error) + "\n")


if __name__ == "__main__":
    raise SystemExit(main())
