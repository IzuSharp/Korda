from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from catalog import Catalog
from native_graph import query, verify


class ModuleCITests(unittest.TestCase):
    def test_old_package_names_are_only_valid_for_the_previous_tree(self):
        modules = {"sqlite": {"package": "NativeWeave.Database.Sqlite", "checks": ["sqlite.test"]}}
        with self.assertRaisesRegex(ValueError, "Invalid or duplicate public package"):
            Catalog(modules)
        self.assertEqual(Catalog(modules, historical=True).packages["sqlite"], modules["sqlite"]["package"])

    @unittest.skipUnless(shutil.which("cmake"), "CMake required")
    def test_configured_cmake_graph_requires_declared_dependency(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            build = root / "build"
            for name in ("consumer", "generator"):
                (root / name).mkdir()
                (root / name / "input").touch()
            (root / "CMakeLists.txt").write_text(
                "cmake_minimum_required(VERSION 3.20)\nproject(Fixture NONE)\n"
                "add_custom_target(gen SOURCES generator/input)\n"
                "add_custom_target(app SOURCES consumer/input)\nadd_dependencies(app gen)\n"
            )
            query(build)
            subprocess.run(
                [
                    "cmake",
                    "-S",
                    str(root),
                    "-B",
                    str(build),
                    "-G",
                    "Ninja" if shutil.which("ninja") else "Unix Makefiles",
                ],
                check=True,
                capture_output=True,
            )
            modules = {
                "compiler": {"sources": ["generator/"], "targets": ["gen"]},
                "gpu": {"sources": ["consumer/"], "targets": ["app"]},
            }
            with self.assertRaisesRegex(ValueError, "app uses unregistered.*compiler"):
                verify(build, Catalog(modules), ["gen", "app"])
            modules["gpu"]["depends"] = ["compiler"]
            verify(build, Catalog(modules), ["gen", "app"])
