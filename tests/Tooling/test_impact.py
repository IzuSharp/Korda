import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from catalog import Catalog, load_catalog
from impact import files, fingerprints, make_plan


class ImpactTests(unittest.TestCase):
    def setUp(self):
        self.catalog = load_catalog()

        mock = patch("impact.public_heads")
        self.registry = mock.start()
        self.addCleanup(mock.stop)

    def plan(self, paths, *, previous=None, stale=(), full=False):
        entries = {p: p.encode() for p in paths}
        hashes = fingerprints(self.catalog, entries)
        self.registry.side_effect = lambda ids: {p: {"version": "0.1.0-preview.1.2",
            "inputs": "old" if p in stale else hashes[p]} for p in ids}
        return make_plan(self.catalog, paths, entries, previous or self.catalog, full)

    def test_every_tracked_file_has_one_owner(self):
        for path in files("HEAD"):
            with self.subTest(path=path):
                self.catalog.owner(path)

    def test_docs_and_planner_have_no_release_or_database_work(self):
        for paths in (["AGENTS.md"], ["eng/ci/impact.py", ".github/workflows/ci.yml", "tests/Tooling/test_impact.py"]):
            result = self.plan(paths, stale=("postgresql", "sqlite"))
            self.assertEqual(("light", False, [], [], []),
                             (result["scope"], result["deep"], result["modules"], result["candidates"], result["checks"]))
        self.registry.assert_not_called()

    def test_public_consumer_edit_executes_candidate_without_releasing(self):
        result = self.plan(["eng/ci/consume.py"])
        self.assertEqual(("pack", True, []), (result["scope"], result["consume"], result["candidates"]))
        self.registry.assert_called_once()

    def test_native_package_ownership_and_unclassified_paths(self):
        for path, expected in {
            "src/Data.NativeHost/Providers/Sqlite/Adapter.cs": {"sqlite"},
            "native/cpp/patches/libpq-explicit-configuration.patch": {"postgresql"},
            "native/cpp/src/provider.cpp": {"sqlite", "postgresql"},
            "src/Query.Fluent/Select.cs": {"sqlite", "postgresql"},
            "eng/ci/bundles.py": {"sqlite", "postgresql"},
            "LICENSE": {"sqlite", "postgresql"},
        }.items():
            with self.subTest(path=path):
                self.assertEqual(expected, set(self.plan([path], stale=expected)["packages"]))
        with self.assertRaisesRegex(ValueError, "Unregistered CI path: src/Unknown"):
            self.plan(["src/Unknown/Entry.cs"])

    def test_test_edits_keep_independent_checks(self):
        for path, scope, checks in [
            ("tests/Data.Sqlite/Program.cs", "sqlite", []),
            ("tests/Data.Postgres/Program.cs", "postgresql", []),
            ("tests/Packaging.Release/ApiCheck/Program.cs", "light", ["pack-api"]),
            ("tests/Packaging.Linux/test_profile.py", "light", ["elf-profile"]),
            ("tests/Query.Generator/Program.cs", "light", ["generator"]),
        ]:
            with self.subTest(path=path):
                result = self.plan([path])
                self.assertEqual((scope, checks, []), (result["scope"], result["checks"], result["packages"]))
        both = self.plan(["tests/Data.Sqlite/Program.cs", "tests/Data.Postgres/Program.cs"])
        self.assertEqual(["sqlite", "postgresql"], both["build_scopes"])
        self.registry.assert_not_called()

    def test_recovery_and_packaging(self):
        paths = ["src/Data.Sqlite/Provider.cs"]
        peer = self.plan(paths, stale={"postgresql"})
        self.assertEqual(("full", ["postgresql"]), (peer["scope"], peer["packages"]))
        single = self.plan(paths, stale={"sqlite"})
        self.assertEqual(("sqlite", ["sqlite"]), (single["scope"], single["packages"]))
        packaging = self.plan(["src/Packaging.Release/BuildTasks/Task.cs"], stale={"sqlite", "postgresql"})
        self.assertEqual(("pack", True, ["pack-api", "pack-metadata"]),
                         (packaging["scope"], packaging["rebuild_task"], packaging["checks"]))
        full = self.plan(["AGENTS.md"], full=True)
        self.assertEqual(("full", True, []), (full["scope"], full["consume"], full["packages"]))

    def test_independent_module_and_top_level_delivery(self):
        self.catalog = Catalog({"compiler": {"sources": ["compiler/"], "tests": ["test-compiler/"], "run": ["compiler-check"]},
                                "gpu": {"sources": ["gpu/"], "run": ["gpu-check"]},
                                "qt": {"sources": ["qt/"], "includes": ["compiler", "gpu"], "package": "NativeWeave.Qt",
                                       "run": ["qt-suite"], "pack": ["pack"], "consume": ["consume"]},
                                **self.catalog.modules})
        tests = self.plan(["test-compiler/new.cpp"])
        self.assertEqual((["compiler"], []), (tests["modules"], tests["candidates"]))
        self.registry.assert_not_called()
        change = self.plan(["compiler/new.cpp"], stale={"qt", "sqlite", "postgresql"})
        self.assertEqual(("light", ["compiler"], ["qt"], []),
                         (change["scope"], change["modules"], change["candidates"], change["packages"]))
        self.assertEqual({"compiler": "run"}, change["run_steps"])
        self.registry.assert_called_with({"qt": "NativeWeave.Qt"})
        top = self.plan(["qt/api.cs"], stale={"qt"})
        self.assertEqual((["compiler", "gpu", "qt"], {"compiler": "run", "gpu": "run", "qt": "run"}), (top["modules"], top["run_steps"]))
        self.catalog.add_project_references([("gpu/gpu.csproj", '<Project><ItemGroup><ProjectReference Include="../compiler/tool.csproj"/></ItemGroup></Project>')])
        self.assertEqual(["compiler", "gpu"], self.plan(["compiler/source.cpp"], stale={"qt"})["modules"])

    def test_removal_with_new_registry_and_invalid_registrations(self):
        before = Catalog({"lib": {"sources": ["old/"]}, "api": {"depends": ["lib"], "run": ["verify"]}})
        after = Catalog({"api": {"tests": ["eng/ci/modules/"], "run": ["verify"]}})
        result = make_plan(after, ["old/source.cpp", "eng/ci/modules/product.json"],
                           {"eng/ci/modules/product.json": b"data"}, before)
        self.assertEqual(["api"], result["modules"])
        with self.assertRaisesRegex(ValueError, "Invalid module dependency"):
            Catalog({"a": {"depends": ["b"]}, "b": {"depends": ["a"]}})
        with self.assertRaisesRegex(ValueError, "Ambiguous ownership"):
            Catalog({"a": {"sources": ["src/"]}, "b": {"tests": ["src/"]}}).owner("src/file")
        with self.assertRaisesRegex(ValueError, "Invalid or duplicate public package"):
            Catalog({"x": {"package": "Other.Name", "pack": ["pack"], "consume": ["run"]}})
