"""Immutable input and future implementation-refinement identity validation."""

from hashlib import sha256
from pathlib import Path
import re
import subprocess


def digest(path):
    """Hash exact source bytes, without normalizing line endings or whitespace."""
    return sha256(Path(path).read_bytes()).hexdigest()


def source_manifest(root):
    """Hash only A19's ordered source/document/example scope; exclude this manifest."""
    root = Path(root)
    paths = []
    for directory in ("src/Verification.Models", "tests/Verification.Models", "samples/api/verification-models"):
        paths.extend(path for path in (root / directory).rglob("*")
                     if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc")
    guide = root / "docs/guides/verification-models.md"
    if guide.is_file():
        paths.append(guide)
    return [{"path": path.relative_to(root).as_posix(), "sha256": digest(path)}
            for path in sorted(paths)]


def verify_design_inputs(pin, design_root):
    """Validate the exact locked Design commit and every declared input hash."""
    design_root = Path(design_root).resolve()
    head = subprocess.check_output(["git", "-C", str(design_root), "rev-parse", "HEAD"], text=True).strip()
    if head != pin["design_commit"]:
        raise ValueError("Design HEAD differs from the task's locked commit")
    validated = []
    for item in pin["files"]:
        relative = Path(item["path"])
        path = (design_root / relative).resolve()
        if relative.is_absolute() or design_root not in path.parents:
            raise ValueError("Design input must remain within the locked repository")
        actual = digest(path)
        if actual != item["sha256"]:
            raise ValueError("Locked Design input hash mismatch: " + item["path"])
        validated.append({"path": item["path"], "sha256": actual})
    return {"status": "verified", "repository": pin["design_repository"], "commit": head, "files": validated}


def verify_refinement(bindings, implementation_root, expected_actions):
    """Bind later A20 refinement to immutable implementation code and tests.

    Merely supplying a source path is insufficient: each transition needs a
    complete commit, tracked source hash, symbol, synchronization rationale and
    a hashed refinement test. This validates identity, not the test's correctness
    or execution. A19 ships no claimed A03/A06/A07/A08 runtime implementation.
    """
    root = Path(implementation_root).resolve()
    commit = bindings.get("commit", "")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Refinement requires a full immutable implementation commit")
    head = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if head != commit:
        raise ValueError("Refinement implementation HEAD does not match its commit")
    rows = bindings.get("transitions", [])
    actions = [row.get("action") for row in rows]
    if set(actions) != set(expected_actions) or len(set(actions)) != len(actions):
        raise ValueError("Refinement transition inventory is incomplete, duplicate or unexpected")
    checked = []
    for row in rows:
        for field in ("symbol", "synchronization", "test_symbol"):
            if not isinstance(row.get(field), str) or not row[field].strip():
                raise ValueError("Refinement requires " + field)
        for path_field, hash_field in (("path", "sha256"), ("test_path", "test_sha256")):
            relative = Path(row[path_field])
            path = (root / relative).resolve()
            if relative.is_absolute() or root not in path.parents:
                raise ValueError("Refinement path escapes the implementation repository")
            declared = row[hash_field]
            if not re.fullmatch(r"[0-9a-f]{64}", declared):
                raise ValueError("Refinement requires a SHA-256 source hash")
            committed = subprocess.check_output(["git", "-C", str(root), "show", commit + ":" + relative.as_posix()])
            if sha256(committed).hexdigest() != declared or digest(path) != declared:
                raise ValueError("Refinement source or test differs from exact committed bytes")
        checked.append(row["action"])
    return {"status": "source_identity_verified_only", "commit": commit,
            "actions": sorted(checked), "executed_refinement_tests": False}
