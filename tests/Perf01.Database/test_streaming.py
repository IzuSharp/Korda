"""Finite synthetic gates for the optional compact preparation/parent path."""
import copy
import importlib.util
import json
from collections import Counter
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
spec = importlib.util.spec_from_file_location("perf01_database_driver", HERE / "run.py")
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)
A21 = driver.load_a21()


def observation(count=204):
    records = []
    for identifier in range(count):
        scheduled = identifier * 1_000_000
        status = "failed" if identifier == 2 else "ok"
        records.append({"id": identifier, "scheduled_ns": scheduled,
                        "dispatch_ns": scheduled + 10, "admission_ns": scheduled + 20,
                        "completion_ns": scheduled + 100 + identifier, "status": status,
                        "value": {"titles": ["snow-雪", "quote-\""], "nested": [{"value": identifier}]},
                        "target_counters": {"payload": {"status": "measured", "value": identifier}},
                        "target_completion_observed_ns": scheduled + 100 + identifier})
    successful = [r["completion_ns"] - r["scheduled_ns"] for r in records if r["status"] == "ok"]
    elapsed = count * 1_000_000
    return {"valid": True, "route": "nativeweave-aot-rust", "repetition": 0,
            "invalid_reasons": [], "hello": {"receipt_directory": "/synthetic/receipt"},
            "offered": count, "admitted": count, "completed": count, "successful": len(successful),
            "outcome_counts": dict(Counter(r["status"] for r in records)),
            "histograms": {"arrival_all_outcomes": {"resolution_ns": 1, "maximum_ns": 1_000_000,
                                                        "bins": {str(i): i for i in range(32)}},
                           "arrival_success": {"bins": {"0": count},
                               "percentiles_from_exact_samples_ns": {
                                   "p50": A21.percentile(successful, .5),
                                   "p95": A21.percentile(successful, .95),
                                   "p99": A21.percentile(successful, .99)}}},
            "measurement_window_ns": {"start": 0, "end": elapsed, "elapsed": elapsed},
            "rates_per_second": {"offered": count * 1e9 / elapsed}, "exit_code": 0,
            "records": records, "late_events": [{"event": {"value": "not retained by the parent"}}]}


def downstream_metrics(row):
    """The existing parent summary inputs, without application or SQL fixtures."""
    records = row["records"]
    sent = [value for value in records if value["dispatch_ns"] is not None]
    kinds = {}
    for kind in ("cycle", "list"):
        selected = [r for r in records if driver.payload("mixed", r["id"], 20261119)["kind"] == kind]
        values = [r["completion_ns"] - r["scheduled_ns"] for r in selected if r["status"] == "ok"]
        kinds[kind] = {"offered": len(selected), "successful": len(values),
                       "outcomes": dict(Counter(r["status"] for r in selected)),
                       "p50_ns": A21.percentile(values, .5), "p95_ns": A21.percentile(values, .95),
                       "p99_ns": A21.percentile(values, .99)}
    return {"span": (sent[-1]["dispatch_ns"] - sent[0]["dispatch_ns"]) / 1e9 if len(sent) > 1 else 0,
            "dispatched": len(sent), "by_kind": kinds, "outcomes": row["outcome_counts"],
            "latency": row["histograms"]["arrival_success"]["percentiles_from_exact_samples_ns"],
            "successful_per_second": row["successful"] * 1e9 / row["measurement_window_ns"]["elapsed"]}


class CompactPreparationAndParentTests(unittest.TestCase):
    def test_streamed_dataset_has_exact_existing_pretty_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            for scenario in ("cycle", "page", "mixed"):
                for count in (0, 1, 4):
                    with self.subTest(scenario=scenario, count=count):
                        output = Path(temporary) / (scenario + str(count) + ".json")
                        expected = json.dumps(driver.dataset(scenario, count, 20261119), indent=2, sort_keys=True) + "\n"
                        with patch.object(driver, "dataset", side_effect=AssertionError("whole dataset must not be allocated")):
                            driver.write_compact_dataset(output, scenario, count, 20261119)
                        self.assertEqual(expected.encode(), output.read_bytes())
                        self.assertFalse(output.with_name(output.name + ".tmp").exists())

    def test_scalar_projection_preserves_validator_statistics_and_field_order_independence(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "observation.json"
            for count in (8, 204):
                for records_first in (False, True):
                    with self.subTest(count=count, records_first=records_first):
                        full = observation(count)
                        if records_first:
                            full = {"records": full["records"], **{k: v for k, v in full.items() if k != "records"}}
                        path.write_text(json.dumps(full, ensure_ascii=False), encoding="utf-8")
                        with patch.object(Path, "read_text", side_effect=AssertionError("parent must stream observation")):
                            compact = driver.load_compact_observation(path)
                        driver.domain.validate_observation(full, full["route"], 0, count, A21)
                        driver.domain.validate_observation(compact, full["route"], 0, count, A21)
                        self.assertEqual(downstream_metrics(full), downstream_metrics(compact))
                        self.assertNotIn("bins", compact["histograms"]["arrival_all_outcomes"])
                        self.assertNotIn("late_events", compact)
                        self.assertTrue(all(set(row) == {"id", "scheduled_ns", "dispatch_ns", "admission_ns", "completion_ns", "status"}
                                            for row in compact["records"]))
                        if count == 8:
                            self.assertIsNone(downstream_metrics(compact)["by_kind"]["cycle"]["p99_ns"])

    def test_invalid_result_verdict_and_unadmitted_dispatches_are_preserved(self):
        full = observation(8)
        full.update(valid=False, invalid_reasons=["generator saturation: dispatch lag"])
        full["records"][-1].update(dispatch_ns=None, admission_ns=None, status="timeout")
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "observation.json"
            path.write_text(json.dumps(full))
            compact = driver.load_compact_observation(path)
        driver.domain.validate_observation(full, full["route"], 0, 8, A21)
        driver.domain.validate_observation(compact, full["route"], 0, 8, A21)
        self.assertFalse(compact["valid"])
        self.assertEqual(full["invalid_reasons"], compact["invalid_reasons"])
        self.assertEqual(downstream_metrics(full), downstream_metrics(compact))

    def test_projected_metadata_skips_bins_without_decoding_the_histogram_object(self):
        reader = driver.load_json_reader()
        original = reader.JsonStream.value

        def guarded_value(instance):
            value = original(instance)
            if isinstance(value, dict) and ("bins" in value or "arrival_all_outcomes" in value):
                raise AssertionError("large histogram metadata must be structurally projected")
            return value

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "observation.json"
            path.write_text(json.dumps(observation(8)))
            with patch.object(driver, "load_json_reader", return_value=reader), patch.object(reader.JsonStream, "value", guarded_value):
                compact = driver.load_compact_observation(path)
            driver.domain.validate_observation(compact, "nativeweave-aot-rust", 0, 8, A21)

    def test_existing_shape_failures_remain_rejected(self):
        changes = {
            "missing ID": lambda r: r["records"][0].pop("id"),
            "Boolean ID": lambda r: r["records"][0].update(id=False),
            "wrong ID": lambda r: r["records"][0].update(id=1),
            "wrong cardinality": lambda r: r["records"].pop(),
            "noninteger time": lambda r: r["records"][0].update(scheduled_ns=0.5),
            "reversed time": lambda r: r["records"][0].update(completion_ns=-1),
            "Boolean stage time": lambda r: r["records"][0].update(admission_ns=True),
            "bad outcome": lambda r: r["records"][0].update(status="invented"),
            "bad count": lambda r: r.update(successful=True),
            "bad outcomes sum": lambda r: r.update(outcome_counts={"ok": 1}),
            "bad histogram scale": lambda r: r["histograms"]["arrival_all_outcomes"].update(resolution_ns=0),
            "bad elapsed": lambda r: r["measurement_window_ns"].update(elapsed=0),
            "bad offered rate": lambda r: r["rates_per_second"].update(offered=float("inf")),
            "bad route": lambda r: r.update(route="wrong"),
            "bad repetition": lambda r: r.update(repetition=False),
            "bad validity": lambda r: r.update(valid=1),
            "bad reasons": lambda r: r.update(invalid_reasons=[1]),
            "missing receipt": lambda r: r.update(hello={}),
        }
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "observation.json"
            for label, mutate in changes.items():
                with self.subTest(label=label):
                    full = observation(8)
                    mutate(full)
                    path.write_text(json.dumps(full))
                    with self.assertRaises(ValueError):
                        driver.domain.validate_observation(full, "nativeweave-aot-rust", 0, 8, A21)
                    compact = driver.load_compact_observation(path)
                    with self.assertRaises(ValueError):
                        driver.domain.validate_observation(compact, "nativeweave-aot-rust", 0, 8, A21)

    def test_malformed_and_duplicate_or_missing_ledgers_fail(self):
        valid = json.dumps(observation(8))
        documents = (valid[:-1], valid + " false", '{"records":[],"records":[]}',
                     '{"records":null}', '{"records":[null]}', '{}',
                     '{"records":[],}',
                     '{"records":[],"histograms":{"arrival_all_outcomes":{"resolution_ns":1,}}}',
                     '{"records":[],"histograms":{"arrival_all_outcomes":{},}}',
                     '{"records":[],"histograms":{"arrival_all_outcomes":[]}}')
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "observation.json"
            for document in documents:
                with self.subTest(document=document[:80]):
                    path.write_text(document)
                    with self.assertRaises(ValueError):
                        driver.load_compact_observation(path)


if __name__ == "__main__":
    unittest.main()
