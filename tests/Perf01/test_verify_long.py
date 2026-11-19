"""Finite synthetic evidence tests; no package, server, database or timed load."""
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import verify_long as verifier


def operation(identifier, scheduled, *, delay=100_000_000):
    return {"id": identifier, "status": "ok", "scheduled_ns": scheduled,
            "dispatch_ns": scheduled + 1_000_000, "admission_ns": scheduled + 2_000_000,
            "completion_ns": scheduled + delay}


class StreamTests(unittest.TestCase):
    def test_chunk_boundaries_and_truncation(self):
        document = json.dumps([{"id": 12345, "escaped": "a\\\"b", "value": -1.5e20}, None])
        for size in (1, 2, 7, 64):
            reader = verifier.JsonStream(io.StringIO(document), chunk=size)
            self.assertEqual(list(reader.array()), json.loads(document))
        with self.assertRaises(ValueError):
            list(verifier.JsonStream(io.StringIO(document[:-1]), chunk=2).array())

    def test_db_nested_array_skips_other_containers(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "observation.json"
            path.write_text(json.dumps({"ignored": [{"records": [999]}], "records": [{"id": 1}], "valid": True, "after": [1, 2, 3]}))
            metadata = {"valid": None}
            self.assertEqual(list(verifier.array_records(path, "records", metadata)), [{"id": 1}])
            self.assertIs(metadata["valid"], True)


class SpanTests(unittest.TestCase):
    def make_span(self, delay=100_000_000, excluded=()):
        span = verifier.Span(101, 18_020_000_000, 100_000_000)
        # Processing order is independent of ID/dispatch order.
        for identifier in reversed(range(101)):
            span.add(operation(identifier, 1_000_000_000 + identifier * span.period, delay=delay), identifier not in excluded)
        return span.finish()

    def test_completion_bound_and_original_span_are_separate(self):
        proof = self.make_span()
        self.assertTrue(proof["duration_proved"])
        self.assertEqual(proof["ordinary_operation_lower_bound_ns"], 1_801_901_000_000)
        self.assertEqual(proof["ipc_send_lower_bound_ns"], 1_801_999_000_000)

    def test_schedule_and_admission_guard_do_not_prove_operations(self):
        proof = self.make_span(delay=3_000_000_000)
        self.assertGreater(proof["ipc_send_lower_bound_ns"], verifier.REQUIRED_NS)
        self.assertFalse(proof["duration_proved"])

    def test_expected_probes_never_extend_the_ordinary_endpoints(self):
        proof = self.make_span(excluded=(0, 100))
        self.assertFalse(proof["duration_proved"])
        self.assertEqual(proof["earliest_ordinary_completion"]["id"], 1)
        self.assertEqual(proof["last_ordinary_dispatch"]["id"], 99)
        self.assertEqual(proof["expected_probe_successes"], 2)

    def test_a_late_first_admission_does_not_discard_a_proven_ordinary_span(self):
        span = verifier.Span(228, 8_000_000_000, 100_000_000)
        for identifier in range(228):
            row = operation(identifier, 1_000_000_000 + identifier * span.period)
            if identifier == 0:
                row.update(admission_ns=21_000_000_000, completion_ns=22_000_000_000)
            span.add(row)
        proof = span.finish()
        self.assertEqual(proof["earliest_ordinary_completion"]["id"], 1)
        self.assertLess(proof["ipc_send_lower_bound_ns"], verifier.REQUIRED_NS)
        self.assertTrue(proof["duration_proved"])
        self.assertTrue(proof["arrival_p99_at_most_500ms_from_raw"])

    def test_missing_duplicate_failed_and_generator_lag_fail(self):
        span = verifier.Span(101, 18_020_000_000, 100_000_000)
        span.add(operation(0, 1_000_000_000))
        with self.assertRaises(ValueError):
            span.add(operation(0, 1_000_000_000))
        with self.assertRaises(ValueError):
            span.finish()
        for changes in ({"status": "timeout"}, {"dispatch_ns": 1_200_000_000, "admission_ns": 1_210_000_000, "completion_ns": 1_300_000_000}):
            with self.assertRaises(ValueError):
                verifier.Span(101, 18_020_000_000, 100_000_000).add(operation(0, 1_000_000_000) | changes)


def codec_fixture(root, *, middle_rss=32 * 1024**2, bad_recovery=False, completion_delay=100_000_000):
    def save(name, value):
        (root / name).write_text(json.dumps(value))
    def lines(name, rows):
        (root / name).write_text("".join(json.dumps(row) + "\n" for row in rows))
    count, period, origin = 102, 18_000_000_000, 5_000_000_000
    sequence = [[0, "c", "a"], [1, "d", "o"]]
    save("plan.json", {"domain": "codec", "source_commit": "a" * 40, "route": "nativeweave", "duration_seconds": 1802,
        "action": "mixed", "interface": "mixed", "period_ns": period, "max_lag_ms": 100, "sequence": sequence,
        "manifest_sha256": "a" * 64, "controller_sha256": "b" * 64, "a21_sha256": "c" * 64,
        "corpus_manifest_sha256": "d" * 64, "actual_loaded_native": [{"path": "/synthetic/libcodec.so", "sha256": "e" * 64}]})
    rows = []
    for index in range(count):
        row = operation(index + 1, origin + index * period, delay=completion_delay)
        case, action, interface = sequence[index % len(sequence)]
        row.update(phase="measurement", case=case, action=action, interface=interface,
                   admitted_ns=row.pop("admission_ns"), outcome=row.pop("status"), terminal_status="ok",
                   target={"id": index + 1, "status": "ok"})
        rows.append(row)
    rows.append({"id": count + 1, "phase": "recovery", "outcome": "failed" if bad_recovery else "ok"})
    lines("operations.jsonl", rows)
    lines("events.jsonl", [{"kind": "drained", "errors": 0, "native": {"live": 0, "capacity": 0, "active": 0}}])
    lines("resources.jsonl", [{"observer_ns": origin + index * period, "target": {"rss_bytes": {
        "availability": "measured", "value": value}}} for index, value in ((1, 32 * 1024**2), (50, middle_rss), (100, 32 * 1024**2))])
    phase = {key: count for key in ("planned", "sent", "dispatch_attempted", "admitted", "completed", "ok")}
    save("result.json", {"valid": True, "complete": True, "service_standard_met": True, "recovery_standard_met": True,
        "invalid_reasons": [], "exit_code": 0, "drained_at_utc": "synthetic", "idle_observation_seconds": 60,
        "scheduled_start_ns": origin, "scheduled_end_ns": origin + (count - 1) * period,
        "actual_dispatch_span_seconds": (count - 1) * period / 1e9,
        "phase_counts": {"measurement": phase, "warmup": {"planned": 1}, "recovery": {"planned": 1}}})
    save("summary.json", {"valid": True, "offered": count, "successes": count, "arrival_success_ns": {"p99": completion_delay},
        "files": {name: verifier.digest(root / name) for name in ("plan.json", "result.json", "events.jsonl", "operations.jsonl", "resources.jsonl")}})


class CodecAcceptanceTests(unittest.TestCase):
    def verify_fixture(self, **options):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            codec_fixture(root, **options)
            return verifier.verify("codec", root)

    def test_complete_finite_evidence_passes(self):
        receipt = self.verify_fixture()
        self.assertTrue(receipt["accepted"], receipt["failure_reasons"])
        self.assertGreaterEqual(receipt["duration_credit_seconds"], 1800)

    def test_midrun_rss_spike_is_not_hidden_by_normal_endpoints(self):
        receipt = self.verify_fixture(middle_rss=verifier.RSS_BYTES + 1)
        self.assertFalse(receipt["accepted"])
        self.assertIn("Measured application RSS exceeds 256 MiB", receipt["failure_reasons"])

    def test_codec_driver_flag_cannot_bypass_tail_or_recovery(self):
        for options in ({"completion_delay": 600_000_000}, {"bad_recovery": True}):
            self.assertFalse(self.verify_fixture(**options)["accepted"])

    def test_changed_raw_hash_and_profiling_marker_fail(self):
        for profile in (False, True):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory); codec_fixture(root)
                if profile:
                    (root / "profile-driver.json").write_text('{"formal_timing_eligible":false}')
                else:
                    with (root / "operations.jsonl").open("a") as stream:
                        stream.write('{"unexpected":"changed"}\n')
                receipt = verifier.verify("codec", root)
                self.assertFalse(receipt["accepted"])
                self.assertEqual(receipt["duration_credit_seconds"], 0)

    def test_malformed_shapes_and_nonfinite_values_leave_failed_receipts(self):
        for corruption in ("summary-array", "target-null", "infinite-lag"):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory); codec_fixture(root)
                summary = json.loads((root / "summary.json").read_text())
                if corruption == "summary-array":
                    (root / "summary.json").write_text("[]")
                else:
                    name = "resources.jsonl" if corruption == "target-null" else "plan.json"
                    if corruption == "target-null":
                        (root / name).write_text('{"observer_ns":23000000000,"target":null}\n')
                    else:
                        plan = json.loads((root / name).read_text()); plan["max_lag_ms"] = float("inf")
                        (root / name).write_text(json.dumps(plan))
                    summary["files"][name] = verifier.digest(root / name)
                    (root / "summary.json").write_text(json.dumps(summary))
                receipt = verifier.verify("codec", root)
                self.assertFalse(receipt["accepted"])
                self.assertTrue(receipt["failure_reasons"])
                self.assertEqual(receipt["duration_credit_seconds"], 0)


def protocol_fixture(root, *, endpoint_probes=False, bad_client=False):
    def save(name, value):
        path = root / name; path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value))
    def lines(name, rows):
        path = root / name; path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("".join(json.dumps(row) + "\n" for row in rows))
    count, period, origin = 102, 18_000_000_000, 5_000_000_000
    tokens = ["http-cancel|http1-tls|8388608|1" if endpoint_probes and i in (0, count - 1) else "http-tiny|http1-tls|0|1" for i in range(count)]
    save("oracle.json", [{"input": token, "expected": token} for token in tokens])
    native = {"/synthetic/libserver.so": "a" * 64}
    save("adapter.json", {"native_sha256": native, "idle_seconds": 60})
    manifest_hash = verifier.digest(root / "adapter.json")
    plan = {"route": "nativeweave-aot-rust", "schedule": {"group": "http", "duration_seconds": 1802},
        "manifest_sha256": manifest_hash, "oracle": {"sha256": verifier.digest(root / "oracle.json")},
        "comparison_identity_sha256": "b" * 64, "artifacts": native,
        "timing": {"requests": count, "period_ns": period, "max_dispatch_lag_ns": 100_000_000},
        "settings": {"warmup_requests": 1, "recovery_requests": 32},
        "service_standard": {"unexpected_error_rate_maximum": 0, "arrival_p99_maximum_ms": 500, "whole_process_rss_bytes_maximum": verifier.RSS_BYTES}}
    save("plan.json", plan)
    rows = [operation(i, origin + i * period) | {"value": tokens[i]} for i in range(count)]
    lines("requests.jsonl", rows)
    lines("execution/client/operations.jsonl", [{"id": i, "scenario": token.split("|")[0], "protocol": "http1-tls",
        "logical_status": "failed" if bad_client and i == 10 else "ok", "correctness_failure": False} for i, token in enumerate(tokens)])
    lines("execution/process-resources.jsonl", [{"monotonic_ns": origin + i * period,
        "server": {"counters": {"rss_bytes": {"availability": "measured", "value": 32 * 1024**2}}}} for i in (1, 50, 100)])
    save("execution/receipt.json", {"status": "passed", "errors": [], "manifest_sha256": manifest_hash,
        "loaded_native_sha256": native, "client_hello": {"dynamic_code_supported": False}, "idle_start_utc_ns": 10, "idle_end_utc_ns": 60_000_000_010,
        "closed": {"status": "Completed", "state": "Disposed", "pending_requests": 0, "retained_managed_bytes": 0, "retained_native_bytes": 0}})
    result = {"status": "completed", "exit_code": 0, "valid": True, "service_standard_met": True,
        "full_30_minute_valid_load": True, "idle_owner_and_request_gauges_receded": True, "invalid_reasons": [],
        "plan_sha256": verifier.digest(root / "plan.json"), "recovery": {"status": "passed", "failures": [], "calls": 32},
        "measurement_window": {"actual_dispatch_span_ns": (count - 1) * period, "start_monotonic_ns": origin, "end_monotonic_ns": rows[-1]["completion_ns"]},
        "histograms": {"arrival_success": {"percentiles_from_exact_samples_ns": {"p99": 100_000_000}}},
        **{key: count for key in ("offered", "actually_dispatched", "admitted", "completed", "successful")}}
    result["evidence_sha256"] = {str(path.relative_to(root)): verifier.digest(path) for path in root.rglob("*") if path.is_file()}
    save("result.json", result)


class ProtocolAcceptanceTests(unittest.TestCase):
    def test_sparse_result_is_streamed_with_complete_hash_and_no_summary_size_gate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); protocol_fixture(root)
            path = root / "result.json"
            full = json.loads(path.read_text())
            bins = {str(index * 1000): index + 1 for index in range(2048)}
            full["histograms"]["arrival_success"].update(bins=bins, overflow_ns=[])
            full["histograms"]["arrival_all_outcomes"] = {"bins": bins}
            full["scenarios"] = {"http-tiny/http1-tls": {"arrival_success": {"bins": bins}}}
            path.write_text(json.dumps(full))
            wanted_hash = verifier.digest(path)
            original_stat, original_read, original_value = Path.stat, Path.read_text, verifier.JsonStream.value

            class OversizedMetadata:
                # Exercise the exact 64 MiB gate without writing a 65 MiB test
                # file. Real JSON spans multiple reader chunks; this is no RSS
                # or large-file performance measurement.
                st_size = 65 * 1024**2

                def __init__(self, actual):
                    self.actual = actual

                def __getattr__(self, name):
                    return getattr(self.actual, name)

            def stat(candidate, *args, **kwargs):
                actual = original_stat(candidate, *args, **kwargs)
                return OversizedMetadata(actual) if candidate == path else actual

            def no_full_text(candidate, *args, **kwargs):
                if candidate == path:
                    raise AssertionError("Protocol result must not be read as a whole string")
                return original_read(candidate, *args, **kwargs)

            def no_decoded_bins(instance):
                value = original_value(instance)
                if isinstance(value, dict) and ("bins" in value or "0" in value):
                    raise AssertionError("Sparse histogram maps must be structurally skipped")
                return value

            with patch.object(Path, "stat", stat):
                with self.assertRaisesRegex(ValueError, "Oversized summary"):
                    verifier.Evidence(root).load("result.json")
                with patch.object(Path, "read_text", no_full_text), patch.object(verifier.JsonStream, "value", no_decoded_bins):
                    receipt = verifier.verify("http", root)
            self.assertTrue(receipt["accepted"], receipt["failure_reasons"])
            self.assertEqual(wanted_hash, receipt["evidence_sha256"]["result.json"])
            self.assertEqual(100_000_000, receipt["driver_arrival_p99_ns"])

    def test_projected_result_keeps_flags_counts_recovery_tail_and_hash_gates(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); protocol_fixture(root)
            path = root / "result.json"; full = json.loads(path.read_text())
            changes = [(name, False) for name in ("valid", "service_standard_met", "full_30_minute_valid_load",
                       "idle_owner_and_request_gauges_receded", "formal_timing_eligible")]
            changes += [(name, 101) for name in ("offered", "actually_dispatched", "admitted", "completed", "successful")]
            changes += [("status", "failed"), ("exit_code", 1), ("invalid_reasons", ["original driver failure"]),
                ("recovery", {"status": "failed", "failures": [], "calls": 32}),
                ("recovery", {"status": "passed", "failures": ["failed probe"], "calls": 32}),
                ("recovery", {"status": "passed", "failures": [], "calls": 31}),
                ("histograms", {"arrival_success": {"percentiles_from_exact_samples_ns": {"p99": 600_000_000}}}),
                ("histograms", {"arrival_success": {"percentiles_from_exact_samples_ns": {}}}),
                ("measurement_window", dict(full["measurement_window"], actual_dispatch_span_ns=1)),
                ("plan_sha256", "0" * 64),
                ("evidence_sha256", dict(full["evidence_sha256"], **{"requests.jsonl": "0" * 64}))]
            for name, value in changes:
                with self.subTest(field=name, value=value):
                    path.write_text(json.dumps(dict(full, **{name: value})))
                    receipt = verifier.verify("http", root)
                    self.assertFalse(receipt["accepted"], name)
                    self.assertEqual(0, receipt["duration_credit_seconds"])
                    self.assertTrue(receipt["failure_reasons"])

    def test_malformed_skipped_result_json_and_missing_metadata_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); protocol_fixture(root)
            path = root / "result.json"; valid = path.read_text()
            documents = (valid[:-1], valid + " {}", "[]",
                         valid[:-1] + ',"ignored":[1,]}',
                         valid.replace('"histograms": {', '"histograms": {"ignored": {"1000": 1,},', 1),
                         valid.replace('"recovery": {', '"recovery": {"ignored": [1,],', 1),
                         '{"status":"completed","histograms":{}}')
            for document in documents:
                with self.subTest(document=document[:100]):
                    path.write_text(document)
                    receipt = verifier.verify("http", root)
                    self.assertFalse(receipt["accepted"])
                    self.assertEqual(0, receipt["duration_credit_seconds"])
                    self.assertTrue(receipt["failure_reasons"])

    def test_actual_frozen_tokens_and_independent_client_are_required(self):
        for options, expected in (({}, True), ({"endpoint_probes": True}, False), ({"bad_client": True}, False)):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory); protocol_fixture(root, **options)
                receipt = verifier.verify("http", root)
                self.assertEqual(receipt["accepted"], expected, receipt["failure_reasons"])
                if options.get("endpoint_probes"):
                    self.assertEqual(receipt["duration_proof"]["expected_probe_successes"], 2)


def database_fixture(root, provider):
    def save(name, value):
        path = root / name; path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value))
    count, period, origin = 102, 18_000_000_000, 5_000_000_000
    executable = "/synthetic/application"
    loaded = {executable: {"sha256": "a" * 64}}
    save("target.json", {"perf01": {"recovery_requests": 32}})
    plan = {"oracle": {"dataset": str(root / "dataset.json")},
        "routes": [{"artifacts": {str(root / "target.json"): verifier.digest(root / "target.json")}}],
        "timing": {"requests": count, "period_ns": period, "max_dispatch_lag_ns": 100_000_000}}
    save("plan.json", plan)
    save("frozen-inputs.json", {"plan_sha256": verifier.digest(root / "plan.json"), "planned_load_seconds": 1818,
        "service_standard": {"max_unexpected_errors": 0, "max_p99_ms": 500, "max_app_rss_mib": 256},
        "route": {"artifacts": {executable: "a" * 64}, "command": [executable], "source": {"commit": "b" * 40}}, "source_files": {}})
    app = root / "applications/one"
    save("applications/one/receipt.json", {"status": "passed", "errors": [], "durable_oracle": {"passed": True},
        "closed": {"databaseResourcesReleased": True, "consumerResourcesReleased": True, "serverState": "Disposed"}})
    save("applications/one/perf01.json", {"status": "passed", "loaded_files": loaded,
        "generator_resource_observer": {"status": "completed", "errors": []},
        "phases": {"recovery": {"operations": [{"status": "ok"}] * 32, "oracle": {"passed": True}},
                   "idle": {"start_ns": 1, "end_ns": 60_000_000_001}}})
    (app / "application").mkdir()
    (app / "application/resources.jsonl").write_text(json.dumps({"phase": "measurement", "counters": {"rss_bytes": {"availability": "measured", "value": 32 * 1024**2}}}) + "\n")
    save("observation.json", {"valid": True, "invalid_reasons": [], "exit_code": 0,
        "records": [operation(i, origin + i * period) for i in range(count)],
        **{key: count for key in ("offered", "admitted", "completed", "successful")}})
    summary = {"valid": True, "qualifies_service_standard": True, "duration_valid": True, "invalid_reasons": [],
        "provider": provider, "scenario": "mixed", "stage": "soak", "route": "nativeweave-aot-rust", "supervision": "completed",
        "frozen_inputs_sha256": verifier.digest(root / "frozen-inputs.json"),
        "application_receipt": {"path": str(app / "receipt.json"), "sha256": verifier.digest(app / "receipt.json")},
        "perf01_receipt": {"path": str(app / "perf01.json"), "sha256": verifier.digest(app / "perf01.json")},
        "actual_dispatch_span_seconds": (count - 1) * period / 1e9, "p99_ns": 100_000_000,
        **{key: count for key in ("offered", "successful", "dispatched")}}
    save("summary.json", summary)
    save("progress.json", {"state": "completed", "summary_sha256": verifier.digest(root / "summary.json")})


class DatabaseAcceptanceTests(unittest.TestCase):
    def test_full_application_receipt_is_hash_pinned_but_only_metadata_is_retained(self):
        for provider in ("sqlite", "postgresql"):
            with self.subTest(provider=provider), tempfile.TemporaryDirectory() as directory:
                root = Path(directory); database_fixture(root, provider)
                path = root / "applications/one/receipt.json"
                full = json.loads(path.read_text())
                full["operation_outcomes"] = [{"id": 2, "status": "ok", "value": {"titles": ["snow-雪"] * 100, "versions": [1] * 100}}]
                def bind(document):
                    path.write_text(document)
                    summary = json.loads((root / "summary.json").read_text())
                    summary["application_receipt"]["sha256"] = verifier.digest(path)
                    (root / "summary.json").write_text(json.dumps(summary))
                    (root / "progress.json").write_text(json.dumps({"state": "completed", "summary_sha256": verifier.digest(root / "summary.json")}))
                original = verifier.Evidence.load
                def no_whole_receipt(instance, name, expected=None):
                    if Path(name) == path:
                        raise AssertionError("Complete application receipt must not use the summary loader")
                    return original(instance, name, expected)
                bind(json.dumps(full))
                with patch.object(verifier.Evidence, "load", no_whole_receipt):
                    accepted = verifier.verify(provider, root)
                self.assertTrue(accepted["accepted"], accepted["failure_reasons"])
                self.assertEqual(verifier.digest(path), accepted["evidence_sha256"]["applications/one/receipt.json"])
                # A changed but valid JSON payload still fails its prior hash.
                path.write_text(json.dumps(full) + " ")
                self.assertFalse(verifier.verify(provider, root)["accepted"])
                for changed in (dict(full, formal_timing_eligible=False), dict(full, errors=["observed failure"]),
                                dict(full, durable_oracle={"passed": False}), dict(full, closed={"serverState": "Stopping"})):
                    bind(json.dumps(changed))
                    self.assertFalse(verifier.verify(provider, root)["accepted"])
                bind(json.dumps(full) + " false")
                self.assertFalse(verifier.verify(provider, root)["accepted"])

    def test_both_provider_arrays_and_durable_oracle(self):
        for provider in ("sqlite", "postgresql"):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory); database_fixture(root, provider)
                receipt = verifier.verify(provider, root)
                self.assertTrue(receipt["accepted"], receipt["failure_reasons"])
                self.assertFalse(verifier.verify("postgresql" if provider == "sqlite" else "sqlite", root)["accepted"])


if __name__ == "__main__":
    unittest.main()
