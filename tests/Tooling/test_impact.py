"""Git input ownership and package fingerprints shared with release verification."""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from catalog import Catalog, load_catalog, git
from facts import files, fingerprints


class OwnershipTests(unittest.TestCase):
    def test_current_tree_ownership(self):
        tree = git("write-tree").decode().strip()
        catalog = load_catalog(tree)
        for path in files(tree):
            with self.subTest(path=path):
                catalog.owner(path)

    def test_unknown_ambiguous_paths_and_cycles(self):
        with self.assertRaisesRegex(ValueError, "Unregistered"):
            Catalog({}).owner("new/source.cpp")
        with self.assertRaisesRegex(ValueError, "Ambiguous"):
            Catalog({"a": {"sources": ["src/"]}, "b": {"tests": ["src/"]}}).owner("src/file")
        with self.assertRaisesRegex(ValueError, "dependency"):
            Catalog({"a": {"depends": ["b"]}, "b": {"depends": ["a"]}})

    def test_compiler_project_reference_affects_package_inputs(self):
        catalog = Catalog(
            {
                "compiler": {"sources": ["compiler/"]},
                "gpu": {"sources": ["gpu/"]},
                "qt": {
                    "sources": ["qt/"],
                    "package": "Korda.Qt",
                    "pack": ["pack"],
                    "consume": ["consume"],
                },
            }
        )
        catalog.add_project_references(
            [
                (
                    "qt/qt.csproj",
                    '<Project><ProjectReference Include="../compiler/compiler.csproj"/></Project>',
                )
            ]
        )

        def digest(source, value):
            return fingerprints(catalog, {source: value})["qt"]

        self.assertNotEqual(
            digest("compiler/source.cpp", b"first"),
            digest("compiler/source.cpp", b"second"),
        )
        self.assertEqual(digest("gpu/source.cpp", b"first"), digest("gpu/source.cpp", b"second"))
