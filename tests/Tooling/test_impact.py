import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from impact import PROVIDERS, plan, rule


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
        self.assertEqual("light", plan(["README.md"], hashes, published)["scope"])
        self.assertEqual([], plan(["tests/Data.Sqlite/Program.cs"], hashes, published)["packages"])
        published["postgresql"]["inputs"] = "previous"
        result = plan(["README.md"], hashes, published)
        self.assertEqual((result["scope"], result["packages"]), ("full", ["postgresql"]))

    def test_test_only_pr_does_not_recover_a_pending_release(self):
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
                actual = plan([path], hashes, old, recover=False)
                self.assertEqual((scope, checks, []), (actual["scope"], actual["checks"], actual["packages"]))
        actual = plan(list(cases)[3:], hashes, old, recover=False)
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
