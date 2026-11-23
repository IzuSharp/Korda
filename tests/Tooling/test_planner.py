"""Impact tests use tracked paths, immutable trees and a registry stub. No credentials are needed."""

import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from catalog import Catalog, git, load_catalog
from facts import files
from impact import make_plan, plan_tree
from plan_run import jobs
from runner import Action, Context, ordered


class RoutingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tree = git("write-tree").decode().strip()
        cls.graph = load_catalog(cls.tree)
        cls.entries = files(cls.tree)

    def plan(self, *paths):
        for path in paths:
            self.assertIn(path, self.entries)
        with patch(
            "impact.public_heads",
            side_effect=lambda ids: {p: {"version": None, "inputs": None} for p in ids},
        ):
            return make_plan(self.graph, paths, self.entries, self.graph)

    def actions(self, plan):
        return {name for actions in plan["actions"].values() for name in actions}

    def test_notes_and_orchestration_do_not_need_product_resources(self):
        for path in (
            "AGENTS.md",
            "tests/Reference.TaskService/README.md",
            ".editorconfig",
            ".clang-format",
            "eng/build.py",
            "eng/postgres-cache-key.py",
            "eng/ci/worker.py",
        ):
            with (
                self.subTest(path=path),
                patch("impact.public_heads", side_effect=AssertionError),
            ):
                result = make_plan(self.graph, [path], self.entries, self.graph)
                self.assertEqual((result["resources"], result["candidates"]), ({}, []))

    def test_abi_fixture_needs_only_the_native_toolchain(self):
        result = self.plan("tests/Interop.Abi/native/bootstrap.c")
        self.assertEqual(result["actions"], {"fixtures": ["fixture.abi-bootstrap"]})
        self.assertEqual(result["resources"], {"fixtures": ["native"]})

    def test_memory_suite_and_native_interop_are_database_free(self):
        result = self.plan("tests/Interop.Memory/verify.py", "native/cpp/tests/interop_tests.cpp")
        self.assertEqual(self.actions(result), {"interop.native", "interop.memory"})
        self.assertEqual(result["resources"]["interop"], ["dotnet", "native"])
        self.assertFalse(result["candidates"])

    def test_provider_source_bootstraps_only_its_own_archive(self):
        plan = self.plan("src/Data.Sqlite/SqliteOptions.cs")
        self.assertEqual(plan["packages"], ["sqlite"])
        self.assertEqual(plan["public_checks"], ["sqlite"])
        self.assertIn("public.validate", self.actions(plan))
        self.assertEqual(plan["resources"]["linux"], ["design", "dotnet", "native"])
        self.assertFalse(plan["grouped_pack"])

    def test_single_public_bundle_reuses_components_required_by_another_check(self):
        plan = self.plan("src/Data.Sqlite/SqliteOptions.cs", "tests/Verification.EndToEnd/api_probe.py")
        self.assertEqual(plan["packages"], ["sqlite"])
        self.assertTrue(plan["grouped_pack"])
        self.assertIn("database.pack", self.actions(plan))
        from database_plan import catalog_steps

        steps = catalog_steps("linux", plan["public_checks"], True, plan["grouped_pack"])
        self.assertEqual(steps["public.pack"].needs, ("database.pack",))

    def test_independent_api_surfaces(self):
        for path, expected in (
            ("src/Data.Sqlite/api-reference.md", "sqlite.api"),
            ("src/Data.Sqlite/public-api.expected.json", "sqlite.api"),
            ("tests/Query.Fluent/ApiCheck/Program.cs", "fluent.api"),
            ("tests/Data.Migrations/api.py", "migrations.api"),
            ("tests/Orm.Relations/api.py", "relations.api"),
            ("public-api-standard.json", "database.api"),
        ):
            with self.subTest(path=path):
                plan = self.plan(path)
                self.assertIn(expected, self.actions(plan))
                self.assertNotIn("database.regressions", self.actions(plan))
                self.assertFalse(plan["packages"])

    def test_shared_code_requires_both_public_consumers(self):
        plan = self.plan("src/Data.Codecs/ParameterSnapshot.cs")
        self.assertEqual(plan["packages"], ["postgresql", "sqlite"])
        self.assertIn("public.consume-aot", self.actions(plan))
        self.assertNotIn("database.native-tests", self.actions(plan))
        self.assertFalse(any(a.startswith("weekly.") for a in self.actions(plan)))

    def test_scoped_migration_tests_only_execute_their_consumer(self):
        result = self.plan("tests/Data.Migrations/cross_process.py")
        self.assertEqual(self.actions(result), {"database.native", "data.migrations.test"})
        self.assertEqual(result["packages"], [])

    def test_runner_strategy_requires_only_backward_edges(self):
        result = self.plan("src/Data.Sqlite/SqliteOptions.cs", "src/Data.Codecs/ParameterSnapshot.cs")
        from database_plan import catalog_steps

        steps = catalog_steps("linux", result["public_checks"], True, result["grouped_pack"])
        for selected in result["actions"].values():
            for index, name in enumerate(selected):
                self.assertLessEqual(set(steps[name].needs), set(selected[:index]))

    def test_historical_images_only_run_in_extended_validation(self):
        plan = self.plan("tests/Packaging.Linux/verify.py", "tests/Migration.Cpp/run.py")
        self.assertEqual(set(plan["deferred"]), {"image.runtime", "manual.migration"})
        self.assertNotIn("qemu", set().union(*plan["resources"].values()))

    def test_weekly_stays_on_host_and_images_require_explicit_full_run(self):
        images = {"image.runtime", "image.clean-build", "image.transfer"}
        weekly = make_plan(self.graph, [], self.entries, self.graph, full=True)
        self.assertTrue(images.isdisjoint(self.actions(weekly)))
        self.assertLessEqual(images, set(weekly["deferred"]))
        self.assertIn("weekly.sanitizers", self.actions(weekly))
        explicit = make_plan(self.graph, [], self.entries, self.graph, full=True, images=True)
        self.assertLessEqual(images, self.actions(explicit))
        self.assertNotIn("manual.migration", self.actions(explicit))

    def test_new_language_and_module_do_not_select_database(self):
        graph = Catalog(
            {
                "compiler": {"sources": ["compiler/"], "run": ["go", "test", "./..."]},
                "qt": {
                    "sources": ["qt/"],
                    "tests": ["QtTests/"],
                    "depends": ["compiler"],
                    "run": ["check"],
                    "integration": ["consumer"],
                    "pack": ["pack"],
                    "consume": ["aot"],
                    "package": "Korda.Qt",
                },
                "gpu": {"sources": ["gpu/"], "run": ["gpu"]},
            }
        )
        entries = {
            "compiler/tool.go": b"one",
            "qt/lib.cpp": b"two",
            "gpu/lib.c": b"three",
            "QtTests/ApiCheck/test.cs": b"four",
        }
        with patch("impact.public_heads", return_value={"qt": {}}):
            plan = make_plan(graph, ["compiler/tool.go"], entries, graph)
            tests = make_plan(graph, ["QtTests/ApiCheck/test.cs"], entries, graph)
        self.assertEqual(plan["changed_languages"], ["go"])
        self.assertEqual(plan["modules"], ["compiler", "qt"])
        self.assertEqual(plan["module_packages"], ["qt"])
        self.assertFalse(plan["actions"])
        self.assertEqual(tests["modules"], ["qt"])
        self.assertFalse(tests["actions"])
        self.assertFalse(tests["candidates"])

    def test_real_git_diff_add_rename_delete_and_unregistered_path(self):
        with tempfile.TemporaryDirectory() as tmp:
            env = {"GIT_INDEX_FILE": str(Path(tmp) / "index")}
            with (
                patch.dict(os.environ, env),
                patch("impact.public_heads", return_value={}),
            ):
                git("read-tree", self.tree)
                old = "src/Data.Sqlite/SqliteOptions.cs"
                blob = self.entries[old].split(b"\t")[0].split()[-1].decode()
                git("update-index", "--force-remove", old)
                git(
                    "update-index",
                    "--add",
                    "--cacheinfo",
                    "100644",
                    blob,
                    "src/Data.Postgres/Moved.cs",
                )
                moved = git("write-tree").decode().strip()
                result = plan_tree(self.tree, moved)
                self.assertEqual(result["packages"], ["postgresql", "sqlite"])
                git(
                    "update-index",
                    "--add",
                    "--cacheinfo",
                    "100644",
                    blob,
                    "tests/Qt/WidgetsTests.cs",
                )
                bad = git("write-tree").decode().strip()
                with self.assertRaisesRegex(ValueError, "Unregistered"):
                    plan_tree(self.tree, bad)

    def test_merge_reuses_only_verified_artifacts(self):
        plan = {
            "actions": {"linux": ["test"]},
            "modules": [],
            "module_packages": [],
            "candidates": ["sqlite"],
        }
        self.assertTrue(jobs(plan, "pull_request", False, "")["build"])
        self.assertFalse(jobs(plan, "push", True, "123")["build"])
        with self.assertRaisesRegex(RuntimeError, "verified PR candidate"):
            jobs(plan, "push", True, "")

    def test_unknown_and_cyclic_actions_and_failed_dependency(self):
        nodes = {
            "a": Action("a"),
            "b": Action("b", needs=("a",)),
            "c": Action("c", needs=("a",)),
        }
        self.assertEqual(ordered(["c", "b"], nodes), ["a", "b", "c"])
        with self.assertRaisesRegex(ValueError, "dependency"):
            ordered(["unregistered"], nodes)
        nodes["a"] = Action("a", needs=("b",))
        with self.assertRaisesRegex(ValueError, "dependency"):
            ordered(["a"], nodes)
        with patch.object(Action, "run", side_effect=ValueError("failure")) as run:
            with self.assertRaisesRegex(ValueError, "failure"):
                Context(Path("plan.json")).execute(nodes, ["a", "b"])
            self.assertEqual(run.call_count, 1)
