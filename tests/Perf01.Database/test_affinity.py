"""Finite procfs snapshot tests; no database, sockets, subprocesses or workload."""
import copy
from pathlib import Path
import unittest
from unittest.mock import patch

import perf01_affinity as affinity


def identity(pid, parent, start):
    return {"pid": pid, "parent_pid": parent, "start_ticks": start, "state": "S"}


class TreeFixture:
    def __init__(self):
        self.live = {10: identity(10, 1, 100), 11: identity(11, 10, 110),
                     12: identity(12, 11, 120), 20: identity(20, 1, 90)}
        self.before_mask = None
        self.mask_calls = []
        self.masks = {}

    def read(self, pid):
        if pid not in self.live:
            raise FileNotFoundError(str(pid))
        return copy.deepcopy(self.live[pid])

    def mask(self, pid):
        self.mask_calls.append(pid)
        if self.before_mask:
            self.before_mask(self, pid)
        value = self.read(pid)
        cpus = self.masks.get(pid, [2, 3])
        return {**value, "main_cpus": cpus, "threads": {str(pid): cpus}, "observed_ns": len(self.mask_calls)}

    def observe(self):
        paths = [Path("/proc") / str(pid) for pid in self.live]
        with patch.object(affinity, "_process_identity", side_effect=self.read), \
             patch.object(affinity, "process_masks", side_effect=self.mask), \
             patch.object(Path, "iterdir", return_value=iter(paths)):
            return affinity.process_group(10)


class AffinityTreeTests(unittest.TestCase):
    def test_complete_recursive_tree_without_children_interface(self):
        fixture = TreeFixture()
        snapshot = fixture.observe()
        self.assertEqual({row["pid"] for row in snapshot["processes"]}, {10, 11, 12})
        self.assertEqual(snapshot["skipped_descendants"], [])
        self.assertEqual(fixture.mask_calls.count(10), 2)
        affinity.require_group(snapshot, [2, 3], 100)

    def test_exited_descendant_and_its_orphan_are_recorded(self):
        fixture = TreeFixture()
        def exit_parent(tree, pid):
            if pid == 11:
                tree.live.pop(11)
                tree.live[12]["parent_pid"] = 1
        fixture.before_mask = exit_parent
        snapshot = fixture.observe()
        self.assertEqual({row["pid"] for row in snapshot["processes"]}, {10})
        self.assertEqual({row["pid"] for row in snapshot["skipped_descendants"]}, {11, 12})
        affinity.require_group(snapshot, [2, 3], 100)

    def test_reused_child_pid_is_not_attributed_to_old_tree(self):
        fixture = TreeFixture()
        def reuse(tree, pid):
            if pid == 11:
                tree.live[11] = identity(11, 1, 200)
        fixture.before_mask = reuse
        snapshot = fixture.observe()
        self.assertEqual({row["pid"] for row in snapshot["processes"]}, {10})
        reasons = {row["pid"]: row["reason"] for row in snapshot["skipped_descendants"]}
        self.assertEqual(reasons[11], "identity_changed_during_masks")
        self.assertEqual(reasons[12], "parent_identity_changed_or_exited")

    def test_root_disappearance_or_reuse_is_always_failure(self):
        for mode in ("gone", "reused"):
            fixture = TreeFixture()
            def change_root(tree, pid):
                if pid == 11:
                    if mode == "gone": tree.live.pop(10, None)
                    else: tree.live[10] = identity(10, 1, 500)
            fixture.before_mask = change_root
            with self.assertRaises((FileNotFoundError, ValueError)):
                fixture.observe()

    def test_observed_bad_child_or_late_root_mask_still_fails(self):
        for mode in ("child", "late-root"):
            fixture = TreeFixture()
            if mode == "child":
                fixture.masks[12] = [0, 1]
            else:
                def change_mask(tree, pid):
                    if pid == 10 and tree.mask_calls.count(10) == 2:
                        tree.masks[10] = [0, 1]
                fixture.before_mask = change_mask
            snapshot = fixture.observe()
            with self.assertRaises(ValueError):
                affinity.require_group(snapshot, [2, 3], 100)

    def test_prior_postmaster_identity_must_match(self):
        with self.assertRaises(ValueError):
            affinity.require_group(TreeFixture().observe(), [2, 3], 99)


if __name__ == "__main__":
    unittest.main()
