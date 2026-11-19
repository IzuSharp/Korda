#!/usr/bin/env python3
"""Finite policy ownership checks; no product, database, network or timed load."""
import copy
import gc
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import weakref

import scheduler_gc as subject


class Node:
    pass


class SchedulerGcTests(unittest.TestCase):
    def test_deferred_keeps_refcount_cleanup_and_child_gc(self):
        threshold = gc.get_threshold()

        def worker():
            self.assertFalse(gc.isenabled())
            node = Node()
            reference = weakref.ref(node)
            del node
            self.assertIsNone(reference())  # Ordinary acyclic refcount cleanup.
            cyclic = Node()
            cyclic.self = cyclic
            reference = weakref.ref(cyclic)
            del cyclic
            self.assertIsNotNone(reference())  # Deferral really can retain cycles.
            process = subprocess.run([sys.executable, "-c", "import gc; print(gc.isenabled())"],
                                     capture_output=True, text=True, check=True)
            self.assertEqual(process.stdout.strip(), "True")
            return 0

        with tempfile.TemporaryDirectory() as directory:
            receipt = {}
            self.assertEqual(subject.run_worker(worker, "deferred", Path(directory) / "receipt.json", receipt), 0)
        self.assertTrue(gc.isenabled())
        self.assertEqual(gc.get_threshold(), threshold)
        self.assertEqual(receipt["collections_during_scope"], [0, 0, 0])
        self.assertEqual(receipt["status"], "completed")
        self.assertTrue(receipt["restored_gc"]["enabled"])

    def test_automatic_keeps_enabled_state(self):
        def worker():
            self.assertTrue(gc.isenabled())
            return 0

        with tempfile.TemporaryDirectory() as directory:
            receipt = {}
            subject.run_worker(worker, "automatic", Path(directory) / "receipt.json", receipt)
        self.assertTrue(receipt["scope_entry_gc"]["enabled"])
        self.assertTrue(receipt["scope_exit_gc"]["enabled"])
        self.assertTrue(gc.isenabled())

    def test_failure_and_python_interrupt_restore_without_claiming_success(self):
        for failure in (RuntimeError("finite failure"), KeyboardInterrupt("finite interruption")):
            with self.subTest(type=type(failure).__name__), tempfile.TemporaryDirectory() as directory:
                def worker():
                    self.assertFalse(gc.isenabled())
                    raise failure

                receipt = {}
                with self.assertRaises(type(failure)) as captured:
                    subject.run_worker(worker, "deferred", Path(directory) / "receipt.json", receipt)
                self.assertIs(captured.exception, failure)
                self.assertTrue(gc.isenabled())
                self.assertEqual(receipt["status"], "failed")
                self.assertTrue(receipt["restored_gc"]["enabled"])

    def test_receipt_rejects_interruption_policy_source_and_identity_mismatch(self):
        selected = subject.policy("deferred")
        process = subject.identity(os.getpid())
        sources = {str(path): subject.digest(path) for path in
                   (Path(subject.__file__).resolve(), subject.DOMAIN / "cell_worker.py",
                    subject.DOMAIN / "common.py", subject.ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")}
        supervision = {"worker_pid": process["pid"], "owned_processes": [dict(process, role="cell-worker")]}
        receipt = {"policy": selected, "policy_sha256": subject.policy_sha256(selected),
                   "plan_sha256": "finite-plan", "process": process, "source_files": sources}
        with tempfile.TemporaryDirectory() as directory:
            subject.run_worker(lambda: 0, "deferred", Path(directory) / "receipt.json", receipt)
        self.assertTrue(subject.valid_receipt(receipt, selected, supervision, "finite-plan", sources))
        mutations = (
            lambda value: value.update(status="running"),
            lambda value: value["process"].update(start_ticks=process["start_ticks"] + 1),
            lambda value: value["scope_exit_gc"].update(enabled=True),
            lambda value: value.update(collections_during_scope=[0, 0, 1]),
            lambda value: value["source_files"].pop(str(subject.DOMAIN / "cell_worker.py")),
            lambda value: value.update(policy=subject.policy("automatic")),
            lambda value: value.pop("restored_gc"),
        )
        for mutate in mutations:
            changed = copy.deepcopy(receipt)
            mutate(changed)
            self.assertFalse(subject.valid_receipt(changed, selected, supervision, "finite-plan", sources))
        self.assertFalse(subject.valid_receipt(None, selected, supervision, "finite-plan", sources))


if __name__ == "__main__":
    unittest.main()
