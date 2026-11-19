"""Finite synthetic compact-target receipts; no process, HTTP or database load."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from perf01_outcomes import OutcomeJournal, save_receipt
import target as adapter
spec = importlib.util.spec_from_file_location("perf01_outcome_driver", HERE / "run.py")
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


def outcomes():
    # Completion order, duplicate ID, warmup ID and all historical outcomes are
    # deliberately retained. A21, not this storage helper, validates IDs/counts.
    return [
        {"id": 1009, "status": "ok", "value": {"kind": "list", "titles": ["seed-0000", "snow-雪", 'quote-"'], "versions": [1, 1, 1], "has_cursor": True}},
        {"id": 2, "status": "failed", "value": {"error": "IntegrityFailure"}},
        {"id": 0, "status": "ok", "value": {"kind": "cycle", "replay_equal": True, "title": "line\nend"}},
        {"id": 2, "status": "cancelled", "value": {"reason": "Earlier failure"}},
        {"id": 1, "status": "rejected", "value": {"status": 503}},
    ]


def receipt(route, rows):
    return {"schema_version": 1, "route": route, "status": "passed", "errors": [],
            "operation_outcomes": rows, "durable_oracle": {"passed": True, "operations": 1},
            "closed": {"serverState": "Disposed", "databaseResourcesReleased": True},
            "after": {"nested": [None, False, {"escaped": "雪\n\\"}]}, "resources": "application/resources.jsonl"}


class FaultStream:
    def __init__(self, inner, fault):
        self.inner, self.fault = inner, fault

    @property
    def closed(self):
        return self.inner.closed

    def write(self, value):
        if self.fault == "write":
            self.inner.write(value[:7])
            raise OSError("synthetic partial append")
        return self.inner.write(value)

    def flush(self):
        if self.fault == "flush":
            raise OSError("synthetic flush failure")
        return self.inner.flush()

    def close(self):
        self.inner.close()
        if self.fault == "close":
            raise OSError("synthetic close failure")


class OutcomeJournalTests(unittest.TestCase):
    def test_complete_receipt_bytes_match_historical_save_for_every_route_and_order(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for route in ("nativeweave-aot-rust", "direct-rust", "managed-aot"):
                for rows in ([], outcomes()):
                    with self.subTest(route=route, count=len(rows)):
                        directory = root / (route + str(len(rows))); directory.mkdir()
                        journal = OutcomeJournal(directory / "outcomes.jsonl")
                        wanted = receipt(route, copy.deepcopy(rows))
                        for row in rows:
                            journal.append(row)
                        actual = receipt(route, journal)
                        save_receipt(directory / "receipt.json", actual, journal)
                        self.assertEqual((json.dumps(wanted, indent=2, sort_keys=True) + "\n").encode(),
                                         (directory / "receipt.json").read_bytes())
                        self.assertEqual(wanted["operation_outcomes"], list(journal.rows()))
                        self.assertEqual(len(rows), journal.count)
                        self.assertEqual("completed", journal.receipt()["status"])
                        self.assertTrue(journal.stream.closed)
                        self.assertFalse((directory / "receipt.json.tmp").exists())

    def test_append_is_a_serialized_snapshot_not_a_retained_mutable_page(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); journal = OutcomeJournal(root / "outcomes.jsonl")
            row = outcomes()[0]; wanted = copy.deepcopy(row)
            journal.append(row)
            row["value"]["titles"].append("not an accepted value")
            journal.seal()
            self.assertEqual([wanted], list(journal.rows()))

    def test_partial_write_flush_close_and_serialization_failures_are_sticky(self):
        for fault in ("write", "flush", "close", "serialization"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); path = root / "receipt.json"
                initial = b'{"status":"running"}\n'; path.write_bytes(initial)
                journal = OutcomeJournal(root / "outcomes.jsonl")
                if fault == "serialization":
                    with self.assertRaises(TypeError):
                        journal.append({"value": object()})
                else:
                    journal.stream = FaultStream(journal.stream, fault)
                    if fault == "write":
                        with self.assertRaises(OSError):
                            journal.append(outcomes()[0])
                        self.assertEqual(0, journal.count)
                    else:
                        journal.append(outcomes()[0])
                with self.assertRaises(ValueError):
                    save_receipt(path, receipt("direct-rust", journal), journal)
                with self.assertRaises(ValueError):
                    journal.seal()
                self.assertTrue(journal.stream.closed)
                self.assertEqual(initial, path.read_bytes())
                self.assertFalse(journal.published)
                self.assertEqual("failed", journal.receipt()["status"])

    def test_raw_corruption_missing_extra_reordered_or_changed_payload_never_publishes(self):
        for alteration in ("truncate", "extra", "blank", "malformed", "value", "reorder"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); path = root / "receipt.json"
                initial = b'{"status":"running"}\n'; path.write_bytes(initial)
                journal = OutcomeJournal(root / "outcomes.jsonl")
                for row in outcomes()[:2]: journal.append(row)
                journal.seal(); wire = journal.path.read_bytes(); lines = wire.splitlines(keepends=True)
                changed = {"truncate": wire[:-1], "extra": wire + lines[0], "blank": wire + b"\n",
                           "malformed": wire + b"{broken}\n", "value": wire.replace(b"seed-0000", b"seed-9999"),
                           "reorder": b"".join(reversed(lines))}[alteration]
                journal.path.write_bytes(changed)
                with self.assertRaises((ValueError, json.JSONDecodeError)):
                    save_receipt(path, receipt("managed-aot", journal), journal)
                self.assertEqual(initial, path.read_bytes())
                self.assertFalse(journal.published)
                with self.assertRaises(ValueError):
                    save_receipt(path, receipt("managed-aot", journal), journal)

    def test_mutation_after_replay_and_output_failure_preserve_original_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); path = root / "receipt.json"
            initial = b'{"status":"running"}\n'; path.write_bytes(initial)
            journal = OutcomeJournal(root / "outcomes.jsonl"); journal.append(outcomes()[0])
            verify = journal.verify_unchanged
            def corrupt_then_verify():
                journal.path.write_bytes(journal.path.read_bytes() + b" \n")
                verify()
            with patch.object(journal, "verify_unchanged", corrupt_then_verify), self.assertRaises(ValueError):
                save_receipt(path, receipt("nativeweave-aot-rust", journal), journal)
            self.assertEqual(initial, path.read_bytes())
            self.assertFalse(journal.published)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); journal = OutcomeJournal(root / "outcomes.jsonl"); journal.append(outcomes()[0])
            path = root / "receipt.json"; path.write_bytes(initial)
            with patch.object(Path, "replace", side_effect=OSError("synthetic replace failure")), self.assertRaises(OSError):
                save_receipt(path, receipt("nativeweave-aot-rust", journal), journal)
            self.assertEqual(initial, path.read_bytes())
            self.assertFalse(journal.published)

    def test_compact_override_is_explicit_and_late_append_invalidates_owner(self):
        with tempfile.TemporaryDirectory() as temporary:
            for enabled in (False, True):
                target = adapter.PerfTarget.__new__(adapter.PerfTarget)
                target.directory = Path(temporary) / str(enabled); target.directory.mkdir()
                target.manifest = {"perf01": {"compact_observation": enabled}}
                target.operations = []; original_operations = target.operations
                target.extra = {"status": "passed"}
                target._outcome_journal = None
                target._original_receipt_save = target._compact_receipt_save = None
                original_save = adapter.http_target.save
                try:
                    target.start_outcome_journal()
                    if not enabled:
                        self.assertIs(original_operations, target.operations)
                        self.assertIs(original_save, adapter.http_target.save)
                        self.assertIsNone(target.finish_outcome_journal())
                        continue
                    adapter.http_target.save(target.directory / "receipt.json", {"status": "running"})
                    self.assertEqual({"status": "running"}, json.loads((target.directory / "receipt.json").read_text()))
                    target.operations.append(outcomes()[0])
                    adapter.http_target.save(target.directory / "receipt.json", receipt("direct-rust", target.operations))
                    with self.assertRaises(ValueError):
                        target.operations.append(outcomes()[1])
                    self.assertIsInstance(target.finish_outcome_journal(), ValueError)
                    self.assertEqual("failed", target.extra["status"])
                    self.assertEqual("failed", target.extra["outcome_journal"]["status"])
                    self.assertIs(original_save, adapter.http_target.save)
                finally:
                    if target._outcome_journal is not None:
                        target.finish_outcome_journal()
                    adapter.http_target.save = original_save


class ApplicationReceiptProjectionTests(unittest.TestCase):
    def test_parent_keeps_exact_consumed_fields_and_never_loads_the_outcome_graph(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            for route in ("nativeweave-aot-rust", "direct-rust", "managed-aot"):
                for status in ("passed", "failed"):
                    full = receipt(route, outcomes()); full["status"] = status
                    for first in (False, True):
                        value = full if not first else {"operation_outcomes": full["operation_outcomes"], **{k:v for k,v in full.items() if k != "operation_outcomes"}}
                        path.write_text(json.dumps(value))
                        reader = driver.load_json_reader(); original = reader.JsonStream.value
                        def scalar_or_metadata(instance):
                            value = original(instance)
                            if isinstance(value, dict) and "value" in value and "id" in value:
                                raise AssertionError("whole outcome must be structurally skipped")
                            return value
                        with patch.object(driver, "load_json_reader", return_value=reader), patch.object(reader.JsonStream, "value", scalar_or_metadata), patch.object(Path, "read_text", side_effect=AssertionError("whole receipt read")):
                            projected = driver.load_compact_application_receipt(path)
                        self.assertEqual({key:full[key] for key in ("status", "durable_oracle", "closed")}, projected)

    def test_truncation_trailing_corruption_and_malformed_skipped_fields_fail(self):
        valid = json.dumps(receipt("managed-aot", outcomes()))
        documents = (valid[:-1], valid + " {}", valid[:-1] + ",}",
                     '{"status":"passed","operation_outcomes":[{},]}',
                     '{"status":"passed","operation_outcomes":[{"nested":1,}]}',
                     '{"status":"passed","closed":{"serverState":"Disposed",}}', '[]')
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            for text in documents:
                with self.subTest(document=text[:64]):
                    path.write_text(text)
                    with self.assertRaises(ValueError):
                        driver.load_compact_application_receipt(path)


if __name__ == "__main__":
    unittest.main()
