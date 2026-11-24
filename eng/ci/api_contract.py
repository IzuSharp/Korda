"""Project reviewed public names, then add the reviewed DB-API-01 surface."""

import json
from pathlib import Path
import re

PIN = "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9"
RELATIVE = "src/Data.NativeHost/db-api-01.api.json"
NAMES = "src/Query.Generator/db-rename-01.names.json"


def rename_attributes(baseline, renames):
    """Apply only the explicit Attribute rename; preserve every other contract field."""
    pattern = re.compile(r"\b(?:" + "|".join(re.escape(name) for name in renames) + r")\b")
    expected = {}
    for entry in baseline.values():
        projected = json.loads(pattern.sub(lambda match: renames[match.group()], json.dumps(entry)))
        ident = projected["id"]
        if ident in expected:
            raise ValueError("Attribute rename collides with a public API: " + ident)
        expected[ident] = projected
    return expected


def naming_migration(root):
    migration = json.loads((Path(root) / NAMES).read_text())
    if migration.get("task") != "DB-Rename-01" or migration.get("namespace") != "Korda.Data":
        raise ValueError("Unexpected public Attribute naming migration")
    renames = migration["attributes"]
    suffixes = (
        "Table",
        "Column",
        "Key",
        "Version",
        "Ignore",
        "Projection",
        "Constructor",
        "Parameter",
        "Codec",
        "UseCodec",
    )
    if renames != {"Weave" + suffix + "Attribute": "Db" + suffix + "Attribute" for suffix in suffixes}:
        raise ValueError("The public Attribute migration requires ten exact Db names")
    return renames


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
            if (
                ident in seen
                or not entry.get("signature")
                or not isinstance(entry.get("implicit_member"), bool)
            ):
                raise ValueError("Duplicate or incomplete additive API declaration: " + ident)
            seen.add(ident)
    renames = naming_migration(root)
    return {
        assembly: list(rename_attributes({entry["id"]: entry for entry in values}, renames).values())
        for assembly, values in groups.items()
    }


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
            expected[ident] = dict(
                entry,
                containing_type=owner,
                kind="NamedType" if prefix == "T" else "Addition",
                name=declaration.split("(", 1)[0].rsplit(".", 1)[-1],
                semantic_policy="DB-API-01",
            )
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
    return merge(rename_attributes(baseline, naming_migration(root)), groups), groups
