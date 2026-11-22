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
