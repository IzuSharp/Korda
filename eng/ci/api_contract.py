"""Add the reviewed DB-API-01 surface without changing any frozen declaration."""

import json
from pathlib import Path
import re

PIN = "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9"
RELATIVE = "src/Data.NativeHost/db-api-01.api.json"


def additions(root):
    contract = json.loads((Path(root) / RELATIVE).read_text())
    if contract.get("task") != "DB-API-01" or contract.get("historical_design") != PIN:
        raise ValueError("Unexpected additive API contract identity")
    if not re.fullmatch(r"[0-9a-f]{40}", contract.get("design_commit", "")):
        raise ValueError("The additive contract requires a fixed Design commit")
    groups = contract["assemblies"]
    if set(groups) != {"Korda.Data", "Korda.Database"}:
        raise ValueError("Unexpected additive API assembly")
    seen = set()
    for values in groups.values():
        for entry in values:
            ident = entry["id"]
            if ident in seen or not entry.get("signature") or not isinstance(entry.get("implicit_member"), bool):
                raise ValueError("Duplicate or incomplete additive API declaration: " + ident)
            seen.add(ident)
    return groups


def merge(baseline, groups):
    """Never use a current declaration to replace or bless a historical one."""
    expected = dict(baseline)
    for values in groups.values():
        for entry in values:
            ident = entry["id"]
            if ident in expected:
                raise ValueError("An additive declaration shadows a frozen API: " + ident)
            # Historical task ownership filters inspect these fields. New entries
            # have a separate mandatory completeness check below.
            prefix, declaration = ident.split(":", 1)
            owner = declaration.split("(", 1)[0].rsplit(".", 1)[0] if prefix != "T" else None
            expected[ident] = dict(entry, containing_type=owner,
                                   kind="NamedType" if prefix == "T" else "Addition",
                                   name=declaration.split("(", 1)[0].rsplit(".", 1)[-1],
                                   semantic_policy="DB-API-01")
    return expected


def validate_additions(groups, assembly, actual):
    """Reject removal or drift even when an old task's ownership filter excludes it."""
    observed = {entry["id"]: entry for entry in actual}
    for expected in groups.get(assembly, ()):
        got = observed.get(expected["id"])
        if got is None or got["signature"] != expected["signature"]:
            raise ValueError("Additive API is missing or changed: " + expected["id"])


def load(root, baseline):
    groups = additions(root)
    return merge(baseline, groups), groups
