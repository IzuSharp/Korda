import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from impact import PROVIDERS, plan, plan_tree, rule


class ImpactTests(unittest.TestCase):
    def test_component_ownership(self):
        for path, packages in {
            "src/Data.NativeHost/Providers/Sqlite/Adapter.cs": ("sqlite",),
            "native/cpp/patches/libpq-explicit-configuration.patch": ("postgresql",),
            "src/Query.Fluent/Select.cs": PROVIDERS, "native/cpp/src/provider.cpp": PROVIDERS,
            "src/Query.Generator/Runtime/Row.cs": PROVIDERS, "LICENSE": PROVIDERS,
            "README.md": (), "tests/Data.Sqlite/Program.cs": (), ".github/workflows/ci.yml": (),
            "src/Reference.TaskService/Program.cs": (), "src/Verification.Models/verify.py": (),
            "src/Packaging.Release/release.py": (), "src/Build.Integration/ApiReference/Program.cs": (),
        }.items():
            with self.subTest(path=path):
                self.assertEqual(packages, rule(path)[1])

    def test_unpublished_change_is_not_lost(self):
        hashes = dict.fromkeys(PROVIDERS, "current")
        published = {p: {"inputs": value} for p, value in hashes.items()}
        published["postgresql"]["inputs"] = "previous"
        result = plan(["src/Data.Sqlite/Provider.cs"], hashes, published)
        self.assertEqual((result["scope"], result["packages"]), ("full", ["postgresql"]))

    def test_docs_merge_skips_registry_even_when_legacy_packages_have_no_fingerprint(self):
        hashes = dict.fromkeys(PROVIDERS, "current")
        legacy = {p: {"version": "0.1.0-preview.163.2", "inputs": None} for p in PROVIDERS}
        with patch("registry.public_heads", return_value=legacy) as registry:
            for result in (plan_tree(["AGENTS.md"], "HEAD"), plan(["AGENTS.md"], hashes, legacy)):
                self.assertEqual(("light", [], [], False),
                                 (result["scope"], result["checks"], result["packages"], result["consume"]))
            registry.assert_not_called()

    def test_explicit_full_validation_still_consumes_published_packages(self):
        published = {p: {"inputs": "previous"} for p in PROVIDERS}
        with patch("registry.public_heads", return_value=published) as registry:
            result = plan_tree(["AGENTS.md"], "HEAD", full=True)
            registry.assert_called_once_with()
        self.assertEqual(("full", True, [], published),
                         (result["scope"], result["consume"], result["packages"], result["published"]))

    def test_test_only_changes_do_not_recover_a_pending_release(self):
        hashes = dict.fromkeys(PROVIDERS, "current")
        old = {p: {"inputs": "previous"} for p in PROVIDERS}
        cases = {
            "README.md": ("light", []),
            "tests/Data.Sqlite/Program.cs": ("sqlite", []),
            "tests/Data.Postgres/Program.cs": ("postgresql", []),
            "tests/Packaging.Release/ApiCheck/Program.cs": ("light", ["pack-api"]),
            "tests/Packaging.Linux/test_profile.py": ("light", ["elf-profile"]),
            "tests/Query.Generator/ApiCheck/Program.cs": ("light", ["generator"]),
        }
        for path, (scope, checks) in cases.items():
            with self.subTest(path=path):
                actual = plan([path], hashes, old)
                self.assertEqual((scope, checks, []), (actual["scope"], actual["checks"], actual["packages"]))
        actual = plan(list(cases)[3:], hashes, old)
        self.assertEqual((actual["scope"], actual["checks"]), ("light", ["elf-profile", "generator", "pack-api"]))

    def test_public_candidates_and_internal_checks_have_independent_inputs(self):
        hashes = dict.fromkeys(PROVIDERS, "current")
        previous = {p: {"inputs": "current"} for p in PROVIDERS}
        previous["sqlite"]["inputs"] = "old"
        actual = plan(["src/Data.Sqlite/Provider.cs", "tests/Query.Generator/Program.cs"], hashes, previous)
        self.assertEqual((actual["scope"], actual["checks"], actual["packages"]),
                         ("sqlite", ["generator"], ["sqlite"]))
        self.assertEqual("full", rule("eng/ci/bundles.py")[0])
        previous["postgresql"]["inputs"] = "old"
        packaging = plan(["src/Packaging.Release/BuildTasks/PackageClosure.cs"], hashes, previous)
        self.assertEqual((packaging["scope"], packaging["checks"], packaging["packages"]),
                         ("pack", ["pack-api", "pack-metadata"], ["postgresql", "sqlite"]))

    def test_two_provider_regression_edits_run_two_independent_jobs(self):
        hashes = dict.fromkeys(PROVIDERS, "same")
        published = {p: {"inputs": "same"} for p in PROVIDERS}
        paths = ["tests/Data.Postgres/Program.cs", "tests/Data.Sqlite/Program.cs", "tests/Query.Generator/Program.cs"]
        result = plan(paths, hashes, published)
        self.assertEqual((list(PROVIDERS), ["generator"], [], False),
                         (result["build_scopes"], result["checks"], result["packages"], result["consume"]))
        self.assertEqual(["full"], plan(paths + ["src/Data.NativeHost/Session.cs"], hashes, published)["build_scopes"])
