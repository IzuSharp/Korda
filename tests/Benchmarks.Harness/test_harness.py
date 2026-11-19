"""Behavioral and corruption tests for A21, independent of unfinished engines."""
import asyncio
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("bench", ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)
reader_spec = importlib.util.spec_from_file_location("perf01_stream_reader", ROOT / "tests/Perf01/verify_long.py")
reader = importlib.util.module_from_spec(reader_spec)
reader_spec.loader.exec_module(reader)


def counters():
    return {name: bench.unknown(unit, "not instrumented in protocol fixture") for name, unit in bench.COUNTERS.items()}


class AccountingTests(unittest.TestCase):
    def test_finite_omission_counterexample(self):
        evidence = bench.omission_counterexample()
        self.assertEqual(1, evidence["service_p99"])
        self.assertEqual(100, evidence["arrival_p99"])
        self.assertEqual(100, len(evidence["completed"]))

    def test_histogram_preserves_overflow_and_rejects_double_correction(self):
        result = bench.histogram([0, 10, 20, 1001], 10, 1000)
        self.assertEqual(4, result["count"])
        self.assertEqual([1001], result["overflow_ns"])
        self.assertEqual(1, result["bins"]["0"])
        self.assertIsNone(result["percentiles_from_exact_samples_ns"]["p99"])
        with self.assertRaisesRegex(ValueError, "double-correct"):
            bench.histogram([100], 1, 1000, correction="expected-interval")
        with self.assertRaises(ValueError):
            bench.histogram([-1], 1, 1000)

    def test_unknown_counter_is_not_zero(self):
        values = counters()
        bench.validate_counters(values)
        values["copied_bytes"]["value"] = 0
        with self.assertRaisesRegex(ValueError, "not zero"):
            bench.validate_counters(values)

    def test_complete_counter_inventory(self):
        values = counters()
        values.pop("native_allocations")
        with self.assertRaisesRegex(ValueError, "exactly"):
            bench.validate_counters(values)

    def test_malformed_counter_and_event_shapes(self):
        for bad in [None, [], 4, "counter"]:
            with self.assertRaisesRegex(ValueError, "object"):
                bench.validate_counters(bad)
        values = counters()
        values["copied_bytes"] = []
        with self.assertRaisesRegex(ValueError, "object"):
            bench.validate_counters(values)
        for bad in [None, [], 4, "event"]:
            with self.assertRaisesRegex(ValueError, "object"):
                bench.RunState(1).event(bad, 0)

    def test_incomplete_tail_is_not_fabricated(self):
        self.assertIsNone(bench.percentile([1] * 99, .99))
        self.assertEqual(1, bench.percentile([1] * 99 + [100], .99))

    def test_duplicate_completion_and_wrong_result(self):
        state = bench.RunState(1)
        state.records[0].update(scheduled_ns=0, dispatch_ns=1)
        state.event({"kind": "admitted", "id": 0}, 2)
        event = {"kind": "completed", "id": 0, "status": "ok", "value": bench.mix64(1), "counters": counters()}
        state.event(event, 3)
        with self.assertRaisesRegex(ValueError, "duplicate"):
            state.event(event, 4)
        broken = bench.RunState(1)
        broken.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2)
        with self.assertRaisesRegex(ValueError, "oracle"):
            broken.event(dict(event, value=0), 3)

    def test_timeout_keeps_late_completion(self):
        state = bench.RunState(1)
        state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2, completion_ns=10, status="timeout")
        state.event({"kind": "completed", "id": 0, "status": "ok", "value": bench.mix64(1), "counters": counters()}, 20)
        self.assertEqual("timeout", state.records[0]["status"])
        self.assertEqual(20, state.late_events[0]["observed_ns"])

    def test_late_event_cannot_win_before_timeout_poller(self):
        state = bench.RunState(1)
        state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2, deadline_ns=10)
        state.event({"kind": "completed", "id": 0, "status": "ok", "value": bench.mix64(1), "counters": counters()}, 11)
        self.assertEqual("timeout", state.records[0]["status"])
        self.assertEqual(11, state.records[0]["completion_ns"])
        self.assertEqual(10, state.records[0]["deadline_ns"])
        self.assertEqual(1, len(state.late_events))

    def test_domain_oracle_requires_exact_dataset_and_preserves_result_shape(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = root / "expected.json"
            data.write_text(json.dumps([{"input": "query-1", "expected": {"rows": 2, "digest": "abc"}}]))
            plan = {"timing": {"requests": 1}, "oracle": {"kind": "expected-values", "dataset": "expected.json", "sha256": bench.sha256(data)}}
            oracle = bench.Oracle(plan, root)
            self.assertEqual("query-1", oracle.input(4))
            state = bench.RunState(1, oracle)
            state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2)
            state.event({"kind": "completed", "id": 0, "status": "ok", "value": {"rows": 2, "digest": "abc"}, "counters": counters()}, 3)
            self.assertEqual("ok", state.records[0]["status"])
            data.write_text("[]")
            with self.assertRaisesRegex(ValueError, "hash"):
                bench.Oracle(plan, root)

    def test_predeclared_noise_band_and_independent_repetitions(self):
        band = {"baseline_id": "finite-test", "absolute_noise_ns": 2, "relative_noise": .01}
        self.assertEqual("regression", bench.noise_comparison([10, 10, 10], [30, 30, 30], band, 42)["decision"])
        self.assertEqual("within_band", bench.noise_comparison([10, 10], [10, 10], band, 42)["decision"])
        self.assertEqual("inconclusive", bench.noise_comparison([10], [30], band, 42)["decision"])

    def test_process_observer_does_not_invent_unavailable_values(self):
        result = bench.observe_process(2 ** 31 - 1)
        self.assertIsNone(result["rss_bytes"]["value"])
        self.assertIsNone(result["all_thread_stacks_bytes"]["value"])


class CompactObservationTests(unittest.TestCase):
    """Identical synthetic events/clocks, with the old live state as the oracle."""

    @staticmethod
    def completed(identifier, status="ok", **extra):
        values = counters()
        values["managed_allocated_bytes"] = bench.measured(identifier + 7, "bytes", "synthetic completion")
        return {"kind": "completed", "id": identifier, "status": status,
                "value": bench.mix64(identifier + 1), "counters": values, **extra}

    @staticmethod
    def visible(state, payloads=True):
        rows = state.records if payloads else [
            {key: value for key, value in row.items() if key not in {"value", "target_counters"}}
            for row in state.records]
        return {"records": rows, "errors": state.errors, "late_events": state.late_events if payloads else None,
                "seen_completion": sorted(state.seen_completion), "last_counters": state.last_counters,
                "last_completion_observed_ns": state.last_completion_observed_ns,
                "last_admission_observed_ns": state.last_admission_observed_ns,
                "in_flight": sorted(state.in_flight), "deadlines": state.deadlines}

    def exercise(self, trace, count, root, mutate_raw=None, rebind_digest=False, streamed=False):
        ordinary = bench.RunState(count)
        compact = bench.RunState(count, compact_completion_payloads=True)
        for state in (ordinary, compact):
            for row in state.records:
                row.update(scheduled_ns=0, dispatch_ns=1, deadline_ns=100, attempts=1)
                state.in_flight.add(row["id"])
            state.deadlines = [(100, identifier) for identifier in range(count)]
        raw = []
        failures = []
        for operation, now, event in trace:
            if operation == "expire":
                ordinary.expire(now); compact.expire(now)
                continue
            wire = event if isinstance(event, bytes) else (json.dumps(event) + "\n").encode()
            index = len(raw)
            raw.append(json.dumps({"observed_ns": now, "wire_base64": base64.b64encode(wire).decode()}))
            outcomes = []
            for state in (ordinary, compact):
                try:
                    options = {"raw_event_index": index} if state is compact else {}
                    state.event(json.loads(wire), now, len(wire), **options)
                    outcomes.append(None)
                except (ValueError, KeyError, TypeError) as error:
                    outcomes.append((type(error).__name__, str(error)))
            self.assertEqual(*outcomes)
            failures.append(outcomes[0])
            self.assertEqual(self.visible(ordinary, False), self.visible(compact, False))
        self.assertTrue(all("target_counters" not in row and "value" not in row for row in compact.records))
        self.assertEqual([], compact.late_events)
        snapshot = copy.deepcopy(self.visible(compact, False))
        path = root / "events.jsonl"
        expected_digest = hashlib.sha256(("\n".join(raw) + "\n").encode()).hexdigest()
        path.write_text("\n".join(mutate_raw(raw) if mutate_raw else raw) + "\n")
        before_hash = bench.sha256(path)
        if streamed:
            index = compact.index_completion_payloads(path, expected_sha256=before_hash if rebind_digest else expected_digest)
            output = root / "observation.json"
            expected = {"records": ordinary.records, "late_events": ordinary.late_events, "last_target_counters": ordinary.last_counters,
                        "resource_samples": [{"nested": [1, {"unicode": "雪"}]}], "histograms": {"bins": {"1000": 2}}}
            actual = {**expected, "records": compact.records, "late_events": compact.late_events}
            bench.write_streamed_observation(output, actual, index)
            self.assertEqual(json.dumps(expected, indent=2) + "\n", output.read_text())
            self.assertEqual(snapshot, self.visible(compact, False))
            self.assertTrue(all("target_counters" not in row and "value" not in row for row in compact.records))
            with self.assertRaisesRegex(ValueError, "one-shot"):
                bench.write_streamed_observation(root / "again.json", actual, index)
            return ordinary, compact, failures
        compact.rehydrate_completion_payloads(path, expected_sha256=before_hash if rebind_digest else expected_digest)
        self.assertEqual(before_hash, bench.sha256(path))
        self.assertEqual(snapshot, self.visible(compact, False))
        # Equality covers every public field, and serialized equality also
        # checks restored record key order and complete late-event metadata.
        self.assertEqual(json.dumps(self.visible(ordinary)), json.dumps(self.visible(compact)))
        with self.assertRaisesRegex(ValueError, "fresh compact"):
            compact.rehydrate_completion_payloads(path, expected_sha256=before_hash)
        return ordinary, compact, failures

    def test_success_and_every_non_success_payload_are_exact(self):
        trace = []
        statuses = ["ok", "failed", "rejected", "cancelled", "timeout", "unsupported"]
        for identifier, status in enumerate(statuses):
            if status == "ok":
                trace.append(("event", 2, {"kind": "admitted", "id": identifier}))
            event = self.completed(identifier, status)
            if status != "ok":
                event["value"] = {"reason": status, "optional": None, "bytes": [identifier, 0]}
            trace.append(("event", 10 + identifier, event))
        with tempfile.TemporaryDirectory() as temporary:
            self.exercise(trace, len(statuses), Path(temporary))

    def test_timeout_poller_and_late_admission_keep_full_late_events(self):
        trace = [("event", 2, {"kind": "admitted", "id": 0}),
                 # Event itself wins the scheduling race with expire(), but
                 # the deadline still wins the logical outcome.
                 ("event", 100, self.completed(0, extension={"trace": [1, 2]})),
                 ("expire", 110, None),
                 ("event", 120, {"kind": "admitted", "id": 1}),
                 ("event", 130, self.completed(1, extension={"late": True})),
                 ("event", 140, self.completed(2, "failed", value={"failure": [3]}))]
        with tempfile.TemporaryDirectory() as temporary:
            ordinary, compact, _ = self.exercise(trace, 3, Path(temporary))
        self.assertEqual(["timeout"] * 3, [row["status"] for row in compact.records])
        self.assertTrue(all("value" not in row for row in compact.records))
        self.assertEqual(3, len(compact.late_events))
        self.assertEqual(ordinary.last_counters, compact.last_counters)

    def test_same_timestamp_duplicates_and_malformed_events_are_not_replayed(self):
        accepted = self.completed(0)
        duplicate = copy.deepcopy(accepted)
        duplicate["counters"]["managed_allocated_bytes"]["value"] = 999
        malformed_counters = self.completed(1)
        malformed_counters["counters"] = []
        trace = [("event", 2, {"kind": "admitted", "id": 0}),
                 ("event", 3, {"kind": "admitted", "id": 1}),
                 ("event", 10, accepted), ("event", 10, duplicate),
                 ("event", 11, malformed_counters),
                 ("event", 11, self.completed(1)),
                 ("event", 12, b"[]\n"), ("event", 13, b"not-json\n"),
                 ("event", 14, {"kind": "admitted", "id": 0}),
                 ("event", 15, self.completed(99))]
        # Direct event callers may catch a rejection and continue. run_cell's
        # separate process tests retain its stricter stop-after-first-error rule.
        with tempfile.TemporaryDirectory() as temporary:
            _, compact, failures = self.exercise(trace, 2, Path(temporary))
        self.assertEqual(6, sum(error is not None for error in failures))
        self.assertEqual(7, compact.records[0]["target_counters"]["managed_allocated_bytes"]["value"])

    def test_missing_or_corrupt_accepted_raw_payload_fails_closed(self):
        trace = [("event", 2, {"kind": "admitted", "id": 0}), ("event", 3, self.completed(0))]

        def wrong_time(rows):
            envelope = json.loads(rows[1]); envelope["observed_ns"] += 1
            return [rows[0], json.dumps(envelope)]

        for mutate in (lambda rows: rows[:1], lambda rows: [rows[0], "[]"], wrong_time,
                       lambda rows: [rows[0], "x" * (128 * 1024 + 1)]):
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaisesRegex(ValueError, "raw events|Accepted completion payload"):
                    self.exercise(trace, 1, Path(temporary), mutate)

    def test_valid_counter_failed_value_and_late_status_tampering_are_rejected(self):
        def changed(rows, update):
            envelope = json.loads(rows[-1])
            event = json.loads(base64.b64decode(envelope["wire_base64"]))
            update(event)
            envelope["wire_base64"] = base64.b64encode((json.dumps(event) + "\n").encode()).decode()
            return rows[:-1] + [json.dumps(envelope)]

        cases = [
            ([("event", 2, {"kind": "admitted", "id": 0}), ("event", 3, self.completed(0))],
             lambda event: event["counters"]["managed_allocated_bytes"].update(value=999)),
            ([("event", 3, self.completed(0, "failed", value={"reason": "original"}))],
             lambda event: event.update(value={"reason": "changed"})),
            ([("event", 2, {"kind": "admitted", "id": 0}), ("event", 101, self.completed(0))],
             lambda event: event.update(status="failed"))]
        for trace, update in cases:
            with self.subTest(trace=trace), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaisesRegex(ValueError, "raw events differ"):
                    self.exercise(trace, 1, Path(temporary), lambda rows: changed(rows, update))

    def test_failed_replay_is_one_shot_even_when_some_late_payloads_were_restored(self):
        state = bench.RunState(1, compact_completion_payloads=True)
        state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2, deadline_ns=10)
        event = self.completed(0)
        state.event(event, 11, raw_event_index=0)
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "events.jsonl"
            wire = (json.dumps(event) + "\n").encode()
            path.write_text(json.dumps({"observed_ns": 11, "wire_base64": base64.b64encode(wire).decode()}) + "\n")
            with self.assertRaisesRegex(ValueError, "raw events differ"):
                state.rehydrate_completion_payloads(path, expected_sha256="0" * 64)
            self.assertEqual(1, len(state.late_events))
            with self.assertRaisesRegex(ValueError, "fresh compact"):
                state.rehydrate_completion_payloads(path, expected_sha256=bench.sha256(path))
            self.assertEqual(1, len(state.late_events))

    def test_malformed_unaccepted_envelopes_are_safely_skipped(self):
        trace = [("event", 2, {"kind": "admitted", "id": 0}),
                 ("event", 3, self.completed(0)), ("event", 4, b"bad-json\n")]
        for replacement in ("[]", '{"observed_ns":4,"wire_base64":"%%%"}', "x" * (128 * 1024 + 1)):
            with self.subTest(replacement=replacement[:20]), tempfile.TemporaryDirectory() as temporary:
                # This fixture declares the malformed envelope as the original
                # persisted bytes, rather than simulating an unbound edit.
                self.exercise(trace, 1, Path(temporary), lambda rows: rows[:2] + [replacement], rebind_digest=True)

    def test_oracle_sharing_keeps_hash_shape_types_and_default_independence(self):
        page = {"titles": ["seed-0000", "seed-0001"], "versions": [1, 1]}
        entries = [{"input": str(i), "expected": copy.deepcopy(value)}
                   for i, value in enumerate([page, page, 1, True, 1.0])]
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); path = root / "oracle.json"
            path.write_text(json.dumps(entries))
            original_hash = bench.sha256(path)
            plan = {"timing": {"requests": len(entries)}, "oracle": {
                "kind": "expected-values", "dataset": path.name, "sha256": original_hash}}
            ordinary = bench.Oracle(plan, root)
            compact = bench.Oracle(plan, root, canonicalize_expected=True)
            self.assertEqual(ordinary.entries, compact.entries)
            self.assertIsNot(ordinary.expected(0), ordinary.expected(1))
            self.assertIs(compact.expected(0), compact.expected(1))
            self.assertEqual([int, bool, float], [type(compact.expected(i)) for i in (2, 3, 4)])
            self.assertEqual(1, compact.expected_canonicalization["reused_entries"])
            self.assertEqual(original_hash, bench.sha256(path))
            path.write_text("[]")
            with self.assertRaisesRegex(ValueError, "hash"):
                bench.Oracle(plan, root, canonicalize_expected=True)

    def test_oracle_cache_bounds_do_not_change_uncached_values(self):
        entries = [{"expected": value} for value in ({"a": [1]}, {"b": [2]}, {"a": [1]},
                   {"huge": "x" * 100}, {"huge": "x" * 100}, list(range(20)), list(range(20)))]
        before = copy.deepcopy(entries)
        report = bench.canonicalize_expected_values(entries, max_unique=1, max_cache_bytes=32,
                                                    max_key_bytes=32, max_nodes=10, max_depth=4)
        self.assertEqual(before, entries)
        self.assertEqual(1, report["unique_cached"])
        self.assertLessEqual(report["cache_key_bytes"], 32)
        self.assertEqual(1, report["reused_entries"])
        self.assertIs(entries[0]["expected"], entries[2]["expected"])
        self.assertIsNot(entries[3]["expected"], entries[4]["expected"])
        self.assertIsNot(entries[5]["expected"], entries[6]["expected"])

    def test_streamed_file_matches_full_rows_late_order_and_unicode_exactly(self):
        trace = [("event", 2, {"kind": "admitted", "id": 0}),
                 ("event", 3, {"kind": "admitted", "id": 1}),
                 ("event", 10, self.completed(1)), ("event", 11, self.completed(0)),
                 ("event", 11, self.completed(0)), ("event", 12, b"not-json\n"),
                 ("expire", 110, None),
                 ("event", 120, self.completed(3, "failed", value={"unicode": "雪" * 1000}, extra=[1, 2])),
                 ("event", 121, self.completed(2, "rejected", value=None))]
        with tempfile.TemporaryDirectory() as temporary:
            self.exercise(trace, 5, Path(temporary), streamed=True)

    def test_streamed_missing_or_modified_raw_never_publishes_final_output(self):
        for mode in ("missing", "wrong-digest", "changed-indexed", "changed-unaccepted"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); raw = root / "events.jsonl"; output = root / "observation.json"
                state = bench.RunState(1, compact_completion_payloads=True)
                state.records[0].update(scheduled_ns=0, dispatch_ns=1, admission_ns=2)
                event = self.completed(0); wire = (json.dumps(event) + "\n").encode()
                line = json.dumps({"observed_ns": 3, "wire_base64": base64.b64encode(wire).decode()}) + "\n"
                tail = "malformed-tail\n"
                raw.write_text(line + tail)
                expected = bench.sha256(raw)
                state.event(event, 3, raw_event_index=0)
                if mode == "missing": raw.write_text(tail)
                if mode in ("missing", "wrong-digest"):
                    with self.assertRaises(ValueError):
                        state.index_completion_payloads(raw, expected_sha256="0" * 64 if mode == "wrong-digest" else expected)
                    with self.assertRaisesRegex(ValueError, "fresh compact"):
                        state.index_completion_payloads(raw, expected_sha256=bench.sha256(raw))
                else:
                    index = state.index_completion_payloads(raw, expected_sha256=expected)
                    raw.write_text(line.replace('"observed_ns": 3', '"observed_ns": 4') + tail if mode == "changed-indexed" else line + "changed-tail!!\n")
                    with self.assertRaisesRegex(ValueError, "changed"):
                        bench.write_streamed_observation(output, {"records": state.records, "late_events": []}, index)
                self.assertFalse(output.exists())

    def test_streaming_oracle_canonicalizes_before_accumulation_and_keeps_input_gates(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); path = root / "oracle.json"
            entries = [{"input": str(i), "expected": {"page": ["row"] * 20}} for i in range(4)]
            path.write_text(json.dumps(entries))
            plan = {"timing": {"requests": 4}, "oracle": {"kind": "expected-values", "dataset": path.name, "sha256": bench.sha256(path)}}
            with patch.object(Path, "read_text", side_effect=AssertionError("Whole oracle read is forbidden")):
                oracle = bench.Oracle(plan, root, canonicalize_expected=True, expected_values_reader=reader.array_records)
            self.assertEqual(entries, oracle.entries)
            self.assertEqual(1, len({id(entry["expected"]) for entry in oracle.entries}))
            self.assertEqual(3, oracle.expected_canonicalization["reused_entries"])
            for bad in (entries[:3], entries + [entries[0]], [{"input": "bad\n", "expected": {}}] * 4,
                        [{"input": True, "expected": {}}] * 4, [{"input": "missing"}] * 4):
                path.write_text(json.dumps(bad)); plan["oracle"]["sha256"] = bench.sha256(path)
                with self.assertRaises((ValueError, TypeError)):
                    bench.Oracle(plan, root, canonicalize_expected=True, expected_values_reader=reader.array_records)
            for suffix in (" trailing", " []"):
                path.write_text(json.dumps(entries) + suffix); plan["oracle"]["sha256"] = bench.sha256(path)
                with self.assertRaises(ValueError):
                    bench.Oracle(plan, root, canonicalize_expected=True, expected_values_reader=reader.array_records)


class ProcessTests(unittest.IsolatedAsyncioTestCase):
    async def fixture(self, fault=None, requests=4, max_in_flight=8, lag=100_000_000, compact=False, streamed=False):
        with tempfile.TemporaryDirectory(prefix="nw-a21-") as temporary:
            base = Path(temporary)
            source = base / "target.py"
            source.write_text('''import importlib.util,json,sys,time
s=importlib.util.spec_from_file_location("bench",sys.argv[1]);b=importlib.util.module_from_spec(s);s.loader.exec_module(b)
c={k:b.unknown(v,"fixture unavailable") for k,v in b.COUNTERS.items()}
print(json.dumps({"kind":"hello","route":"managed-aot","dynamic_code_supported":False}),flush=True)
for line in sys.stdin:
 i,value,mode=line.strip().split("\\t");i=int(i)
 if mode=="malformed":
  print("[]",flush=True);continue
 print(json.dumps({"kind":"admitted","id":i}),flush=True)
 if mode=="timeout":continue
 if mode=="late":time.sleep(.08)
 c["managed_allocated_bytes"]=b.measured(i+1,"bytes","fixture observation")
 event={"kind":"completed","id":i,"status":"ok" if mode in ["corrupt","late"] else mode,"value":0 if mode=="corrupt" else b.mix64(int(value)),"counters":c}
 print(json.dumps(event),flush=True)
''')
            plan = {"timing": {"requests": requests, "period_ns": 5_000_000, "timeout_ns": 50_000_000,
                               "max_dispatch_lag_ns": lag, "max_in_flight": max_in_flight, "cleanup_timeout_ns": 500_000_000},
                    "settings": {"warmup_requests": 0, "instrumentation": "protocol fixture"},
                    "histogram": {"resolution_ns": 1000, "maximum_ns": 1_000_000_000}, "workload_kind": "harness-smoke",
                    "faults": fault or {}}
            route = {"id": "managed-aot", "command": [sys.executable, str(source), str(ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")], "artifacts": {}}
            options = {"compact_completion_payloads": True, "canonicalize_expected": True} if compact else {}
            if streamed:
                options.update(stream_compact_output=True, expected_values_reader=reader.array_records)
            result = await bench.run_cell(plan, route, 0, base, base / "result.json", **options)
            self.assertTrue((base / "result.json").exists())
            if streamed:
                self.assertTrue(all("target_counters" not in row and "value" not in row for row in result["records"]))
                self.assertEqual(requests, result["streamed_observation_return"]["records_written"])
                result = json.loads((base / "result.json").read_text())
                self.assertNotIn("streamed_observation_return", result)
            result["test_raw_lines"] = [json.loads(line) for line in (base / result["raw_events"]["path"]).read_text().splitlines()]
            return result

    async def test_actual_dispatch_and_all_outcomes_are_retained(self):
        result = await self.fixture({"1": "failed", "2": "rejected", "3": "timeout"})
        self.assertTrue(result["valid"], result["invalid_reasons"])
        self.assertEqual({"ok": 1, "failed": 1, "rejected": 1, "timeout": 1}, result["outcome_counts"])
        self.assertEqual(4, result["histograms"]["arrival_all_outcomes"]["count"])
        self.assertEqual(4, result["offered"])
        self.assertEqual(4, result["admitted"])
        self.assertEqual(3, result["completed"])

    async def test_integrity_fault_stops_further_dispatch_and_retains_rows(self):
        result = await self.fixture({"0": "corrupt"})
        self.assertFalse(result["valid"])
        self.assertTrue(any("oracle" in error for error in result["invalid_reasons"]))
        self.assertEqual(4, len(result["records"]))
        self.assertIn("cancelled", result["outcome_counts"])

    async def test_generator_capacity_invalidates_cell(self):
        result = await self.fixture({"0": "timeout"}, max_in_flight=1)
        self.assertFalse(result["valid"])
        self.assertIn("generator saturation: in-flight capacity", result["invalid_reasons"])
        self.assertEqual(4, result["offered"])

    async def test_generator_dispatch_lag_invalidates_cell(self):
        result = await self.fixture(lag=1)
        self.assertFalse(result["valid"])
        self.assertIn("generator saturation: dispatch lag", result["invalid_reasons"])

    async def test_malformed_event_is_persisted_before_rejection(self):
        result = await self.fixture({"0": "malformed"})
        self.assertFalse(result["valid"])
        self.assertEqual(b"[]\n", base64.b64decode(result["test_raw_lines"][0]["wire_base64"]))
        self.assertEqual(4, len(result["records"]))

    async def test_every_completion_counter_is_retained(self):
        result = await self.fixture(requests=2)
        self.assertEqual([1, 2], [row["target_counters"]["managed_allocated_bytes"]["value"] for row in result["records"]])

    async def test_compact_process_rehydrates_without_weakening_integrity_or_late_outcomes(self):
        result = await self.fixture(requests=2, compact=True)
        self.assertTrue(result["valid"], result["invalid_reasons"])
        self.assertEqual([1, 2], [row["target_counters"]["managed_allocated_bytes"]["value"] for row in result["records"]])
        for fault in ("corrupt", "malformed"):
            result = await self.fixture({"0": fault}, compact=True)
            self.assertFalse(result["valid"])
            self.assertIn("cancelled", result["outcome_counts"])
        late = await self.fixture({"0": "late"}, requests=1, compact=True)
        self.assertEqual("timeout", late["records"][0]["status"])
        self.assertEqual(1, len(late["late_events"]))
        self.assertGreaterEqual(late["measurement_window_ns"]["end"], late["late_events"][0]["observed_ns"])
        self.assertNotIn("value", late["records"][0])

    async def test_late_completion_throughput_uses_observed_endpoint(self):
        result = await self.fixture({"0": "late"}, requests=1)
        self.assertEqual("timeout", result["records"][0]["status"])
        self.assertEqual(1, result["completed"])
        self.assertGreaterEqual(result["measurement_window_ns"]["end"], result["late_events"][0]["observed_ns"])
        self.assertAlmostEqual(1e9 / result["measurement_window_ns"]["elapsed"], result["rates_per_second"]["completed"])
        self.assertLess(result["rates_per_second"]["completed"], 15)

    async def test_streamed_process_publishes_full_schema_and_preserves_invalid_outcomes(self):
        for faults, count in (({}, 2), ({"0": "late"}, 1), ({"0": "corrupt"}, 4), ({"0": "malformed"}, 4)):
            result = await self.fixture(faults, requests=count, compact=True, streamed=True)
            if not faults:
                self.assertTrue(result["valid"], result["invalid_reasons"])
                self.assertEqual([1, 2], [row["target_counters"]["managed_allocated_bytes"]["value"] for row in result["records"]])
            elif faults["0"] == "late":
                self.assertEqual("timeout", result["records"][0]["status"])
                self.assertEqual(1, len(result["late_events"]))
            else:
                self.assertFalse(result["valid"])
                self.assertIn("cancelled", result["outcome_counts"])


if __name__ == "__main__":
    unittest.main()
