#!/usr/bin/env python3
"""Synthetic integrity controls; these tests confer no real-query/plan credit."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import plan_diagnostics as diagnostics
import postgres_auto_explain as extension


def manifest_fixture():
    find = {"postgresql": 'SELECT "Id" FROM orders WHERE "Id"=$1 LIMIT $2',
            "sqlite": 'SELECT "Id" FROM orders WHERE "Id"=? LIMIT ?'}
    key = [{"name": "id", "kind": "int64", "value": 4}, {"name": "take", "kind": "int64", "value": 2}]
    return {"queries": [{"name": "read", "sql": find, "parameters": key, "expectedRows": 1},
        {"name": "tx-commit", "expectedRows": 2, "sql": find, "parameters": key, "lifecycle": {
            "begin": {"postgresql": "BEGIN ISOLATION LEVEL READ COMMITTED READ WRITE", "sqlite": "BEGIN IMMEDIATE"},
            "steps": [{"name": "find-original", "sql": find, "parameters": key},
                      {"name": "find-changed", "sql": find, "parameters": key},
                      {"name": "commit", "sql": {"postgresql": "COMMIT", "sqlite": "COMMIT"}, "parameters": []}],
            "independentOrmSql": {"FindSql": 'SELECT "Id" FROM orders WHERE "TenantId"=@tenant AND "Id"=@id'}}}]}


def raw_fixture(manifest, warmup=0):
    return {"ActualCaseIds": [q["name"] for q in manifest["queries"]], "Concurrency": 1, "EmptyDriver": False,
            "Started": len(manifest["queries"]), "Completed": len(manifest["queries"]), "Failed": 0,
            "Rows": sum(q["expectedRows"] for q in manifest["queries"]),
            "WarmupPerWorker": warmup, "Cases": [{"Name": q["name"], "Started": 1, "Succeeded": 1,
            "Failed": 0, "Rows": q["expectedRows"]} for q in manifest["queries"]]}


def log_records(manifest, route="korda_after", cycles=1):
    result = []
    for _ in range(cycles):
        for leg in diagnostics.contract_legs(manifest, "postgresql", route):
            values = diagnostics._expected_values(leg)
            parameter_log = ", ".join(f"${i} = " + ("NULL" if value is None else "'" + value.replace("'", "''") + "'")
                                      for i, value in enumerate(values, 1))
            outer = {"pid": 42, "session_id": "synthetic-session", "message": "execute stmt: " + leg["normalizedSql"]}
            if parameter_log:
                outer["detail"] = "Parameters: " + parameter_log
            result.append(outer)
            if leg["requiresPlan"]:
                plan = {"Query Text": leg["normalizedSql"], "Plan": {"Node Type": "Synthetic Scan"}}
                if parameter_log:
                    plan["Query Parameters"] = parameter_log
                result.append({"pid": 42, "session_id": "synthetic-session",
                               "message": "duration: 0.001 ms  plan:\n" + json.dumps(plan)})
    return result


class ParserTests(unittest.TestCase):
    def collect(self, manifest, records, raw=None, route="korda_after"):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "postgres.json"
            path.write_text("".join(json.dumps(row) + "\n" if isinstance(row, dict) else row + "\n" for row in records))
            return diagnostics.collect_postgres(path, manifest, route, raw or raw_fixture(manifest))

    def test_sql_normalization_preserves_literals_identifiers_and_reused_parameters(self):
        a = diagnostics.normalize_sql('SELECT "MixedCase",\'literal $2 ; -- x\' FROM t WHERE a=@id OR b=@id /* x */;')
        b = diagnostics.normalize_sql('select "MixedCase",\'literal $2 ; -- x\' from t where a=$1 or b=$1')
        self.assertEqual(a, b)
        self.assertNotEqual(a, diagnostics.normalize_sql('SELECT "mixedcase",\'literal $2 ; -- x\' FROM t WHERE a=$1 OR b=$1'))
        with self.assertRaises(ValueError):
            diagnostics.normalize_sql("SELECT 1; DELETE FROM t")

    def test_placeholder_text_inside_literal_is_not_a_binding(self):
        manifest = {"queries": [{"name": "literal", "sql": {"sqlite": "SELECT ' $1 '", "postgresql": "SELECT ' $1 '"},
                                 "parameters": [], "expectedRows": 1}]}
        self.assertEqual(diagnostics.contract_legs(manifest, "sqlite", "direct_cpp")[0]["parameters"], [])

    def test_parameter_parser_quotes_commas_null_empty_unicode_backslash(self):
        text = "$1 = 'a,b''c\\x', $2 = NULL, $3 = 'NULL', $4 = '', $5 = '蟹🦀'"
        self.assertEqual(diagnostics.parse_parameter_log(text), ["a,b'c\\x", None, "NULL", "", "蟹🦀"])
        for text in ("$2 = 'x'", "$1 = 'x', $1 = 'y'", "$1 = 'x'...", "$1 = 'unterminated", "$1 = 4", "$1 = NULL, "):
            with self.subTest(text=text), self.assertRaises(ValueError):
                diagnostics.parse_parameter_log(text)

    def test_actual_nested_json_and_every_lifecycle_leg_are_retained(self):
        manifest = manifest_fixture()
        rows = log_records(manifest, cycles=2)
        evidence = self.collect(manifest, rows, raw_fixture(manifest, warmup=2))
        self.assertTrue(evidence["passed"], evidence["errors"])
        self.assertEqual(len(evidence["outerJsonLog"]), len(rows))
        self.assertEqual(evidence["completeLegCount"], 5)
        self.assertIsNone(evidence["genericCustomPlanCounts"])
        self.assertFalse(evidence["timingComparisonEligible"])
        shared = next(item for item in evidence["signatureCoverage"] if item["requiredPlans"])
        self.assertEqual(shared["requiredPlans"], 6)

    def test_one_shared_lookup_cannot_cover_multiple_legs(self):
        manifest = manifest_fixture()
        rows = log_records(manifest)
        # Remove exactly one actual plan, while every case and SQL key remains.
        del rows[5]
        evidence = self.collect(manifest, rows)
        self.assertFalse(evidence["passed"])
        self.assertTrue(any("Missing SQL/parameters/plan occurrences" in item for item in evidence["errors"]))

    def test_parameter_change_or_missing_detail_cannot_match(self):
        manifest = manifest_fixture()
        for change in ("wrong-value", "missing-detail"):
            rows = log_records(manifest)
            if change == "wrong-value":
                rows[0]["detail"] = "$1 = '999', $2 = '2'"
            else:
                del rows[0]["detail"]
            self.assertFalse(self.collect(manifest, rows)["passed"])

    def test_malformed_outer_log_preserved_and_rejected(self):
        manifest = manifest_fixture()
        evidence = self.collect(manifest, log_records(manifest) + ["{truncated"])
        self.assertFalse(evidence["passed"])
        self.assertEqual(evidence["unparsedLines"][0]["text"], "{truncated")

    def test_valid_multistatement_setup_plan_is_retained_without_business_credit(self):
        manifest = manifest_fixture()
        setup = {"Query Text": "SELECT version(); SHOW server_encoding", "Plan": {"Node Type": "Result"}}
        extra = {"pid": 99, "message": "duration: 0.001 ms  plan:\n" + json.dumps(setup)}
        evidence = self.collect(manifest, [extra, *log_records(manifest)])
        self.assertTrue(evidence["passed"], evidence["errors"])
        self.assertIn(0, evidence["unmatchedPlanIndexes"])
        self.assertEqual(evidence["plans"][0]["autoExplain"], setup)
        self.assertFalse(self.collect(manifest, [extra])["passed"])

    def test_missing_case_counter_row_mismatch_and_empty_driver_fail(self):
        manifest = manifest_fixture()
        for reason in ("missing", "rows", "empty", "duplicate"):
            raw = raw_fixture(manifest)
            if reason == "missing":
                raw["Cases"].pop()
            elif reason == "rows":
                raw["Cases"][0]["Rows"] += 1
            elif reason == "empty":
                raw["EmptyDriver"] = True
            else:
                raw["Cases"].append(raw["Cases"][0])
            self.assertFalse(self.collect(manifest, log_records(manifest), raw)["passed"])

    def test_all_35_manifest_routes_cover_every_lifecycle_leg(self):
        manifest = json.loads((Path(__file__).parent / "workload/queries-v3.json").read_text())
        for route in diagnostics.ROUTES:
            with self.subTest(route=route):
                legs = diagnostics.contract_legs(manifest, "postgresql", route)
                self.assertEqual(len({leg["caseId"] for leg in legs}), 35)
                self.assertGreater(len(legs), 50)
                evidence = self.collect(manifest, log_records(manifest, route, 11), raw_fixture(manifest, 350), route)
                self.assertTrue(evidence["passed"], evidence["errors"])

    def test_independent_lifecycle_keeps_find_sql_difference_and_null_bind(self):
        manifest = json.loads((Path(__file__).parent / "workload/queries-v3.json").read_text())
        legs = diagnostics.contract_legs(manifest, "sqlite", "independent_csharp_orm")
        find = next(item for item in legs if item["legId"] == "tx-commit/find-original")
        self.assertNotIn("limit", find["normalizedSql"])
        self.assertEqual([p["name"] for p in find["parameters"]], ["tenant", "id"])
        update = next(item for item in legs if item["legId"] == "partial-update/change")
        self.assertIsNone(update["parameters"][0]["value"])
        self.assertEqual(update["parameters"][0]["kind"], "nullable-string")

    def test_consumer_reported_sql_mismatch_rejected(self):
        manifest = manifest_fixture()
        raw = raw_fixture(manifest)
        raw["ActualDiagnosticSql"] = {"read": 'SELECT "Id" FROM wrong_table WHERE "Id"=$1 LIMIT $2'}
        self.assertFalse(self.collect(manifest, log_records(manifest), raw)["passed"])

    def test_top_level_failure_or_counter_disagreement_is_not_hidden_by_case_successes(self):
        manifest = manifest_fixture()
        for fields in ({"Succeeded": 1, "Failed": 1}, {"Rows": 1}, {"Started": 999}, {"Completed": None}):
            raw = raw_fixture(manifest)
            raw.update(fields)
            with self.subTest(fields=fields):
                evidence = self.collect(manifest, log_records(manifest), raw)
                self.assertFalse(evidence["passed"])

    def test_bad_typed_parameter_rejected_without_fake_completion(self):
        manifest = manifest_fixture()
        manifest["queries"][0]["parameters"][0]["kind"] = "unknown"
        evidence = self.collect(manifest, [])
        self.assertFalse(evidence["passed"])
        self.assertIn("Unsupported typed parameter", evidence["errors"][0])

    def test_sqlite_replay_never_executes_business_statements(self):
        calls = []

        class FakeLibrary:
            @staticmethod
            def sqlite3_libversion(): return b"synthetic"
            @staticmethod
            def sqlite3_sourceid(): return b"synthetic"
            @staticmethod
            def sqlite3_compileoption_get(index): return None
            @staticmethod
            def sqlite3_total_changes64(db): return 0
            @staticmethod
            def sqlite3_get_autocommit(db): return 1

        class FakeConnection:
            def __init__(self, library, database): self.lib, self.db = FakeLibrary(), None
            def query(self, sql, parameters=None, explain=False):
                calls.append(sql)
                if explain:
                    if not sql.startswith("EXPLAIN QUERY PLAN "):
                        raise AssertionError("Attempted business execution")
                    return [["1", "0", "0", "SYNTHETIC PLAN"]]
                if not sql.startswith("PRAGMA "):
                    raise AssertionError("Unexpected non-explain SQL")
                return []
            def close(self): pass

        manifest = manifest_fixture()
        with tempfile.TemporaryDirectory() as temp, patch.object(diagnostics, "_Sqlite", FakeConnection):
            library, database = Path(temp) / "engine", Path(temp) / "database"
            library.write_bytes(b"synthetic-engine")
            database.write_bytes(b"synthetic-db")
            evidence = diagnostics.replay_sqlite(library, database, manifest, "korda_after", raw_fixture(manifest))
        self.assertTrue(evidence["passed"], evidence["errors"])
        self.assertFalse(evidence["actualDispatchTrace"])
        self.assertEqual(len(evidence["legs"]), 5)
        self.assertFalse(any(sql.startswith(("BEGIN", "COMMIT", "UPDATE", "ROLLBACK")) for sql in calls))

    def test_empty_wal_creation_is_visible_but_nonempty_change_is_rejected(self):
        class FakeLibrary:
            sqlite3_libversion = staticmethod(lambda: b"synthetic")
            sqlite3_sourceid = staticmethod(lambda: b"synthetic")
            sqlite3_compileoption_get = staticmethod(lambda index: None)
            sqlite3_total_changes64 = staticmethod(lambda db: 0)
            sqlite3_get_autocommit = staticmethod(lambda db: 1)

        for payload in (b"", b"nonempty-modification"):
            class FakeConnection:
                def __init__(self, library, database):
                    self.lib, self.db = FakeLibrary(), None
                    Path(str(database) + "-wal").write_bytes(payload)
                def query(self, sql, parameters=None, explain=False):
                    return [["1", "0", "0", "synthetic"]] if explain else []
                def close(self): pass
            with tempfile.TemporaryDirectory() as temp, patch.object(diagnostics, "_Sqlite", FakeConnection):
                library, database = Path(temp) / "engine", Path(temp) / "database"
                library.write_bytes(b"synthetic-engine")
                database.write_bytes(b"synthetic-db")
                manifest = manifest_fixture()
                evidence = diagnostics.replay_sqlite(library, database, manifest, "direct_cpp", raw_fixture(manifest))
            self.assertFalse(evidence["databaseBytesUnchanged"])
            self.assertEqual(evidence["passed"], payload == b"")
            self.assertEqual(evidence["emptyWalFileSetChange"] is not None, payload == b"")


class ExtensionInputTests(unittest.TestCase):
    def test_existing_bad_archive_is_not_downloaded_or_replaced(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            prefix = root / "install"
            prefix.mkdir()
            (prefix / "korda-postgresql-build.json").write_text(json.dumps({"compiler": "unused"}))
            archive = root / "bad.tar.bz2"
            archive.write_bytes(b"wrong-existing-input")
            with patch.object(extension, "_verify_install", return_value={}), patch.object(extension.urllib.request, "urlopen") as download:
                evidence = extension.build(prefix, archive, root / "out")
            self.assertFalse(evidence["passed"])
            self.assertEqual(archive.read_bytes(), b"wrong-existing-input")
            download.assert_not_called()
            self.assertTrue((root / "out/build-receipt.json").is_file())

    def test_output_inside_install_is_rejected_before_write(self):
        with tempfile.TemporaryDirectory() as temp:
            prefix = Path(temp) / "install"
            prefix.mkdir()
            with self.assertRaises(ValueError):
                extension.build(prefix, Path(temp) / "archive", prefix / "diagnostic")
            self.assertFalse((prefix / "diagnostic").exists())


if __name__ == "__main__":
    unittest.main()
