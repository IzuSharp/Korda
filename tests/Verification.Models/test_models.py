"""Regression oracles for models, search completeness and immutable replay."""

from collections import deque
from dataclasses import replace
from hashlib import sha256
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Verification.Models"))

from checker import Model, Transition, explore, replay
from identity import source_manifest, verify_refinement
from suite import models, oracle_met


def graph_model(edges, goals=(), milestones=()):
    return Model("checker-regression", 0,
                 lambda state: (Transition(str(target), "test", target) for target in edges.get(state, [])),
                 lambda state: (), lambda state: state in goals, lambda state: (),
                 tuple(milestones), {"states": len(edges)}, ("Finite explicit test graph.",))


class SearchTests(unittest.TestCase):
    def test_successful_path_is_not_universal_progress(self):
        result = explore(graph_model({0: [1, 2], 1: [], 2: []}, goals=(1,)))
        self.assertEqual(result["status"], "violated")
        self.assertEqual(result["progress"]["reason"], "non_goal_deadlock")
        self.assertEqual(result["progress"]["counterexample"]["steps"][-1]["state"], 2)

    def test_cycle_with_exit_does_not_become_a_liveness_proof(self):
        result = explore(graph_model({0: [0, 1], 1: []}, goals=(1,)))
        self.assertEqual(result["status"], "inconclusive")
        self.assertEqual(result["progress"]["reason"], "non_goal_cycle")

    def test_universal_finite_progress_reports_longest_path(self):
        result = explore(graph_model({0: [1, 2], 1: [3], 2: [4], 4: [3], 3: []}, goals=(3,)))
        self.assertEqual(result["status"], "passed")
        self.assertEqual(result["progress"]["initial_maximum_progress_steps"], 3)

    def test_missing_success_witness_is_a_failure(self):
        result = explore(graph_model({0: []}, goals=(0,), milestones=("usable_result",)))
        self.assertEqual(result["status"], "violated")
        self.assertEqual(result["missing_milestones"], ["usable_result"])

    def test_state_budget_exhaustion_is_inconclusive(self):
        result = explore(graph_model({0: [1], 1: []}, goals=(1,)), max_states=1)
        self.assertEqual(result["status"], "inconclusive")
        self.assertEqual(result["limit_reached"], "state_limit")
        self.assertFalse(result["exhaustive_safety_search"])

    def test_timeout_is_inconclusive(self):
        result = explore(graph_model({0: []}, goals=(0,)), timeout_seconds=1e-12)
        self.assertEqual(result["status"], "inconclusive")
        self.assertEqual(result["limit_reached"], "time_limit")

    def test_duplicate_transition_labels_are_rejected(self):
        problem = replace(graph_model({}), successors=lambda state: [Transition("same", "A19", 1), Transition("same", "A19", 2)])
        with self.assertRaisesRegex(ValueError, "unique"):
            explore(problem)


class ProtocolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.problems = models()
        cls.results = {model.name: explore(model) for model in cls.problems}

    def test_all_candidates_are_exhaustive_and_have_bounded_progress(self):
        for model in self.problems:
            if model.expected_violation:
                continue
            with self.subTest(model=model.name):
                result = self.results[model.name]
                self.assertTrue(oracle_met(model, result), result)
                self.assertEqual(result["states_discovered"], result["states_expanded"])
                self.assertGreater(result["goal_states"], 0)
                self.assertGreater(result["progress"]["maximum_progress_steps"], 0)

    def test_each_unsafe_control_finds_its_named_counterexample(self):
        for model in self.problems:
            if not model.expected_violation:
                continue
            with self.subTest(model=model.name):
                result = self.results[model.name]
                self.assertTrue(oracle_met(model, result), result)
                trace = json.loads(json.dumps(result["counterexamples"][model.expected_violation]))
                state = replay(model, trace)
                self.assertIn(model.expected_violation, list(model.violations(state)))

    def test_legacy_lost_wake_has_the_critical_ordering(self):
        trace = self.results["mailbox/stale_unschedule"]["counterexamples"]["lost_wake_dirty_without_runnable_slot"]
        actions = [step["action"] for step in trace["steps"]]
        read = actions.index("coordinator.read_dirty_before_unschedule")
        stale = actions.index("coordinator.unschedule_from_stale_snapshot")
        self.assertIn("producer.publish_dirty_and_schedule_reserved_slot", actions[read + 1:stale])
        final = trace["steps"][-1]["state"]
        self.assertTrue(final["dirty"])
        self.assertFalse(final["scheduled"])
        self.assertEqual(final["worker"], "idle")

    def test_registration_omission_is_a_cleanup_contract_failure(self):
        errors = self.results["retirement/omit_registration"]["counterexamples"]
        self.assertIn("cleanup_success_without_managed_registration_drain", errors)
        self.assertNotIn("access_after_free", errors)

    def test_tampered_trace_cannot_be_reused_as_evidence(self):
        model = next(problem for problem in self.problems if problem.name == "mailbox/stale_unschedule")
        trace = json.loads(json.dumps(self.results[model.name]["counterexamples"][model.expected_violation]))
        trace["steps"][-1]["state"]["dirty"] = False
        with self.assertRaisesRegex(ValueError, "absent or ambiguous"):
            replay(model, trace)

    def test_transition_owner_map_covers_exact_candidate_actions(self):
        mapping = json.loads((ROOT / "src/Verification.Models/transition-map.json").read_text())
        mapped = {row["action"]: row["owner_task"] for row in mapping["refinement_boundaries"]}
        observed = {}
        for model in self.problems:
            if model.expected_violation:
                continue
            family = model.name.split("/")[0]
            seen = {model.initial}
            queue = deque(seen)
            while queue:
                for edge in model.successors(queue.popleft()):
                    observed[family + ":" + edge.action] = edge.owner
                    if edge.target not in seen:
                        seen.add(edge.target)
                        queue.append(edge.target)
        self.assertEqual(mapped, observed)

    def test_retirement_does_not_admit_later_native_terminal_sources(self):
        model = next(problem for problem in self.problems if problem.name == "results/candidate")
        for phase in ("retired", "released"):
            state = replace(model.initial, native=phase, outcome="success", notice="received")
            actions = [edge.action for edge in model.successors(state)]
            self.assertNotIn("native.reject_second_terminal_cas", actions)
            self.assertNotIn("callback.duplicate_notice_does_not_overwrite_latch", actions)

    def test_no_application_api_or_binary_is_added_by_a19(self):
        mapping = json.loads((ROOT / "src/Verification.Models/transition-map.json").read_text())
        self.assertEqual(mapping["public_api_applicability"]["new_public_application_types"], [])
        self.assertEqual(mapping["public_api_applicability"]["new_managed_assemblies"], [])
        self.assertFalse(list((ROOT / "src/Verification.Models").rglob("*.cs")))


class IdentityTests(unittest.TestCase):
    def test_manifest_excludes_generated_bytecode_and_itself(self):
        manifest = source_manifest(ROOT)
        paths = [item["path"] for item in manifest]
        self.assertEqual(paths, sorted(paths))
        self.assertEqual(len(paths), len(set(paths)))
        self.assertFalse(any("__pycache__" in path or path.endswith(".pyc") for path in paths))
        self.assertIn("src/Verification.Models/identity.py", paths)

    def test_refinement_rejects_stale_commit_source_and_missing_actions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = b"# implementation fixture\n"
            test = b"# refinement test fixture\n"
            (root / "source.py").write_bytes(source)
            (root / "test.py").write_bytes(test)
            subprocess.run(["git", "init", "-q", str(root)], check=True, capture_output=True)
            subprocess.run(["git", "-C", str(root), "add", "source.py", "test.py"], check=True, capture_output=True)
            subprocess.run(["git", "-C", str(root), "-c", "user.name=A19 identity test", "-c",
                            "user.email=a19-test@example.invalid", "commit", "-qm", "fixture"], check=True, capture_output=True)
            commit = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
            bindings = {"commit": commit, "transitions": [{"action": "admission:gate", "path": "source.py",
                        "sha256": sha256(source).hexdigest(), "symbol": "fixture", "synchronization": "one lock",
                        "test_path": "test.py", "test_sha256": sha256(test).hexdigest(), "test_symbol": "check_fixture"}]}
            checked = verify_refinement(bindings, root, ["admission:gate"])
            self.assertFalse(checked["executed_refinement_tests"])
            with self.assertRaisesRegex(ValueError, "inventory"):
                verify_refinement(bindings, root, ["admission:gate", "missing"])
            stale = dict(bindings, commit="0" * 40)
            with self.assertRaisesRegex(ValueError, "HEAD"):
                verify_refinement(stale, root, ["admission:gate"])
            (root / "source.py").write_bytes(source + b"# changed\n")
            with self.assertRaisesRegex(ValueError, "differs"):
                verify_refinement(bindings, root, ["admission:gate"])

    def test_cli_returns_exit_two_for_inconclusive_search(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run([sys.executable, str(ROOT / "src/Verification.Models/verify.py"),
                                     "--model", "mailbox/candidate", "--max-states", "1",
                                     "--output-dir", str(Path(directory) / "run")],
                                    cwd=ROOT, capture_output=True, text=True)
            self.assertEqual(result.returncode, 2, result.stderr)
            report = json.loads((Path(directory) / "run/summary.json").read_text())
            self.assertEqual(report["status"], "inconclusive")
            self.assertFalse(report["complete_suite"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
