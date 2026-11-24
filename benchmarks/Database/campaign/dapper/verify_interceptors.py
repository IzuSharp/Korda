#!/usr/bin/env python3
"""Fail closed if a concrete campaign ORM call lacks a Dapper.AOT interceptor.

This deliberately supports the pinned 1.1.0 generator's location format and this
adapter's small preprocessor vocabulary. It is a build artifact check, not a
substitute for executing every case against the shared expected result.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
from datetime import datetime, timezone
from pathlib import Path


CALL = re.compile(r"\.\s*(?P<method>Query\w*|Execute\w*)\b(?=\s*(?:<[^<>\n]*>)?\s*\()")
LOCATION = re.compile(
    r'InterceptsLocationAttribute\("(?P<path>(?:[^"\\]|\\.)*)",\s*(?P<line>\d+),\s*(?P<column>\d+)\)'
)
PINNED = {"Dapper": "2.1.79", "Dapper.AOT": "1.1.0"}
ROW_TYPES = (
    "TextRow", "MoneyRow", "JsonRow", "WideRow", "JoinRow", "CustomerTotalRow",
    "ProductTotalRow", "RankRow", "RunningRow", "DetailRow", "RelatedRow", "StateRow", "LifecycleRow",
)
PARAMETER_TYPES = ("CommonTypedArgs", "LiteralTypedArgs", "RelatedParameters96",
                   "StateKeyArgs", "StateSetArgs", "StateNoteArgs", "StateConflictArgs")


def identity(path: Path) -> dict:
    content = path.read_bytes()
    return {"path": str(path.resolve()), "bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}


def factory_body(generated: str, name: str) -> str:
    # Strip ordinary generated C# string literals/comments only for brace counting.
    masked = re.sub(r'"(?:[^"\\]|\\.)*"|//[^\n]*|/\*.*?\*/', lambda m: " " * len(m.group()), generated, flags=re.S)
    match = re.search(r"\bclass\s+" + re.escape(name) + r"\s*:[^{]*\{", masked)
    if match is None:
        return ""
    start = match.end() - 1; level = 1
    for index in range(start + 1, len(masked)):
        if masked[index] == "{": level += 1
        elif masked[index] == "}": level -= 1
        if level == 0: return generated[start:index + 1]
    return ""


def active_lines(path: Path, provider: str):
    """Retain original locations while selecting only the compiled provider arm."""
    defined = "SQLITE" if provider == "sqlite" else "POSTGRESQL"
    active = True
    stack: list[list[bool]] = []
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        directive = line.lstrip()
        if directive.startswith("#if ") or directive.startswith("#elif "):
            is_else_if = directive.startswith("#elif ")
            symbol = directive.split(maxsplit=1)[1].strip()
            if symbol not in ("SQLITE", "POSTGRESQL"):
                raise ValueError(f"Unsupported preprocessor expression at {path}:{number}: {symbol}")
            matches = symbol == defined
            if is_else_if:
                if not stack:
                    raise ValueError(f"Unpaired #elif at {path}:{number}")
                parent, already_matched = stack[-1]
                active = parent and not already_matched and matches
                stack[-1][1] |= matches
            else:
                stack.append([active, matches])
                active = active and matches
        elif directive == "#else":
            if not stack:
                raise ValueError(f"Unpaired #else at {path}:{number}")
            parent, already_matched = stack[-1]
            active = parent and not already_matched
            stack[-1][1] = True
        elif directive == "#endif":
            if not stack:
                raise ValueError(f"Unpaired #endif at {path}:{number}")
            active = stack.pop()[0]
        elif directive.startswith("#error") and active:
            raise ValueError(f"Active #error at {path}:{number}")
        elif active:
            yield number, line
    if stack:
        raise ValueError(f"Unclosed preprocessor conditional: {path}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--provider", choices=("sqlite", "postgresql"), required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--generated", type=Path, required=True)
    parser.add_argument("--lock-file", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--copy-generated", action="store_true")
    args = parser.parse_args()
    errors: list[str] = []
    sources = sorted(args.source.glob("*.cs"))
    common = sorted((args.source.parent / "common").glob("*.cs"))
    calls = []
    for source in sources:
        for line_number, line in active_lines(source, args.provider):
            # The finite adapter has no ORM expressions in comments or string literals.
            if line.lstrip().startswith("//"):
                continue
            for match in CALL.finditer(line):
                method = match.group("method")
                if method not in ("QueryUnbufferedAsync", "ExecuteAsync"):
                    errors.append(f"Unreviewed ORM method {source.name}:{line_number}: {method}")
                calls.append({"file": source.name, "line": line_number,
                              "column": match.start("method") + 1, "method": method})

    generated_files = [args.generated] if args.generated.is_file() else sorted(
        p for p in args.generated.rglob("*.generated.cs")
        if "Dapper.CodeAnalysis.DapperInterceptorGenerator" in str(p)
    )
    if len(generated_files) != 1:
        errors.append(f"Expected one current Dapper generated source, found {len(generated_files)}")
    generated = generated_files[0].read_text(encoding="utf-8-sig") if len(generated_files) == 1 else ""
    locations = []
    for match in LOCATION.finditer(generated):
        # The generator emits ordinary C# path literals; JSON decodes the same
        # path escapes for the Linux and Windows forms supported here.
        path = json.loads('"' + match.group("path") + '"')
        method_start = generated.find("internal static ", match.end())
        method_end = generated.find("\n        }", method_start)
        body = generated[method_start:method_end]
        factory = re.search(r"\b(CommandFactory\d+)\.Instance(\d*)\b", body)
        locations.append({"file": Path(path.replace("\\", "/")).name,
                          "line": int(match.group("line")), "column": int(match.group("column")),
                          "command_factory": factory.group(1) if factory else "DefaultCommandFactory",
                          "cache_instance_index": factory.group(2) if factory else ""})
    keys = [(item["file"], item["line"], item["column"]) for item in locations]
    for call in calls:
        key = (call["file"], call["line"], call["column"])
        call["interceptor_count"] = keys.count(key)
        if call["interceptor_count"] != 1:
            errors.append(f"Expected one interceptor for {call}, found {call['interceptor_count']}")
        else:
            location = locations[keys.index(key)]
            call["command_factory"] = location["command_factory"]
            call["cache_instance_index"] = location["cache_instance_index"]
            call["cache_enabled"] = bool(location["cache_instance_index"])
            expected_cache = call["file"] == "PreparedReads.cs"
            if call["cache_enabled"] != expected_cache:
                errors.append(f"CacheCommand policy differs from prepared/dynamic call-site separation: {call}")
            if expected_cache:
                definition = factory_body(generated, location["command_factory"])
                if not all(symbol in definition for symbol in ("TryReuseThreadStatic", "TryRecycleThreadStatic", "DbCommandCache", "ThreadStatic")):
                    errors.append(f"Prepared factory lacks actual public CacheCommand reuse/recycle implementation: {call}")
    source_keys = {(item["file"], item["line"], item["column"]) for item in calls}
    if set(keys) != source_keys:
        errors.append("Generated interceptor locations do not exactly equal the active source ORM call sites")
    read_count = sum(call["method"] == "QueryUnbufferedAsync" for call in calls)
    expected_reads = 52 if args.provider == "postgresql" else 51
    if read_count != expected_reads:
        errors.append(f"The frozen dynamic/prepared adapter and separate lifecycle probes require {expected_reads} explicit typed read calls; found {read_count}")
    cache_count = sum(call.get("cache_enabled", False) for call in calls)
    if cache_count != 36:
        errors.append(f"Expected exactly 36 cached prepared data call sites, found {cache_count}")
    if not re.search(r"DapperAotExtensions\.Command\(", generated):
        errors.append("Generated code does not dispatch to the Dapper.AOT command API")
    if re.search(r"Dapper\.SqlMapper\.(Query|Execute)\w*", generated):
        errors.append("Generated code contains a vanilla Dapper query/execute fallback")
    required_symbols = list(ROW_TYPES) + list(PARAMETER_TYPES) + ["SettingsHandler"]
    if args.provider == "sqlite":
        required_symbols.append("CentsHandler")
    for symbol in required_symbols:
        if not re.search(r"\b" + re.escape(symbol) + r"\b", generated):
            errors.append(f"Generated code does not reference {symbol}")

    lock = json.loads(args.lock_file.read_text())
    packages = lock["dependencies"]["net11.0"]
    required_versions = dict(PINNED)
    if args.provider == "sqlite":
        required_versions.update({"Microsoft.Data.Sqlite.Core": "11.0.0",
                                  "SQLitePCLRaw.provider.sqlite3": "2.1.11",
                                  "SQLitePCLRaw.core": "3.0.5"})
    else:
        required_versions["Npgsql"] = "11.0.0"
    for name, version in required_versions.items():
        if packages.get(name, {}).get("resolved") != version:
            errors.append(f"Package graph differs from the frozen probe: {name} must be {version}")

    report = {
        "schema_version": 1,
        "status": "fail" if errors else "pass",
        "scope": "source call-site interception and locked package graph; correctness and performance require actual execution",
        "captured_utc": datetime.now(timezone.utc).isoformat(),
        "provider": args.provider,
        "source_files": [identity(path) for path in sources + common],
        "generated_files": [identity(path) for path in generated_files],
        "lock_file": identity(args.lock_file),
        "packages": packages,
        "calls": calls,
        "generated_location_count": len(locations),
        "read_call_count": read_count,
        "prepared_cached_call_count": cache_count,
        "cache_structure": "Each generated prepared call-site owns ThreadStatic command slots and a default soft-capacity-16 DbCommandCache. Dynamic sites have neither. This gate does not claim a 16+N total cache bound.",
        "required_generated_symbols": required_symbols,
        "nullable_handler_correctness": "requires execution against the shared expected rows and digest",
        "errors": errors,
    }
    if args.binary is not None:
        if not args.binary.is_file():
            report["errors"].append(f"Published executable is missing: {args.binary}")
            report["status"] = "fail"
        else:
            report["binary"] = identity(args.binary)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.copy_generated and len(generated_files) == 1:
        saved = args.output.parent / "dapper-interceptors.generated.cs"
        shutil.copyfile(generated_files[0], saved)
        report["saved_generated_source"] = identity(saved)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"status": report["status"], "provider": args.provider,
                      "read_calls": read_count, "all_calls": len(calls),
                      "output": str(args.output), "errors": report["errors"]}))
    return 1 if report["status"] != "pass" else 0


if __name__ == "__main__":
    raise SystemExit(main())
