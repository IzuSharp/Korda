import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from catalog import Catalog
from impact import plan_tree
from native_graph import query, verify
from plan_run import jobs


class ModuleCITests(unittest.TestCase):
    def test_git_diff_cross_module_rename_and_solution_membership(self):
        with tempfile.TemporaryDirectory() as temp, patch("catalog.ROOT", Path(temp)):
            root = Path(temp)
            def write(name, data):
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(data)
            def git(*args):
                return subprocess.check_output(["git", "-C", temp, *args], text=True).strip()
            def commit():
                git("add", ".")
                git("-c", "user.name=CI", "-c", "user.email=ci@example.invalid", "commit", "-qm", "fixture")
                return git("rev-parse", "HEAD")

            git("init", "-q")
            write("eng/ci/modules/test.json", json.dumps({
                "ci": {"tests": ["eng/ci/", "NativeWeave.slnx"]},
                "first": {"sources": ["first/"], "run": ["check"]},
                "second": {"sources": ["second/"], "run": ["check"]}}))
            for name in ("first", "second"):
                write(f"{name}/app.csproj", "<Project/>")
            project = '<Project Path="first/app.csproj"/>'
            write("NativeWeave.slnx", "<Solution>" + project + "</Solution>")
            write("first/source.cpp", "test")
            base = commit()
            write("NativeWeave.slnx", "<Solution>" + project + '<Project Path="second/app.csproj"/></Solution>')
            commit()
            self.assertEqual(["second"], plan_tree(base)["modules"])
            base = git("rev-parse", "HEAD")
            (root / "first/source.cpp").rename(root / "second/new.cpp")
            commit()
            plan = plan_tree(base)
            self.assertEqual(["first", "second"], plan["modules"])
            self.assertEqual([], plan["candidates"])
            self.assertEqual({"build": False, "focused": False, "modules": True}, jobs(plan, "pull_request", False, ""))
            self.assertFalse(any(jobs(plan, "push", True, "").values()))

    def test_main_promotes_or_rebuilds_only_the_selected_family(self):
        plan = {"scope": "light", "checks": [], "modules": [], "module_packages": ["new"],
                "packages": [], "candidates": ["new"]}
        self.assertEqual({"build": False, "focused": False, "modules": True}, jobs(plan, "push", True, ""))
        self.assertFalse(any(jobs(plan, "push", True, "prior-run").values()))
        self.assertFalse(any(jobs(plan, "push", False, "").values()))

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
                "add_custom_target(app SOURCES consumer/input)\nadd_dependencies(app gen)\n")
            query(build)
            subprocess.run(["cmake", "-S", str(root), "-B", str(build), "-G", "Ninja" if shutil.which("ninja") else "Unix Makefiles"],
                           check=True, capture_output=True)
            modules = {"compiler": {"sources": ["generator/"], "targets": ["gen"]},
                       "gpu": {"sources": ["consumer/"], "targets": ["app"]}}
            with self.assertRaisesRegex(ValueError, "app uses unregistered.*compiler"):
                verify(build, Catalog(modules), ["gen", "app"])
            modules["gpu"]["depends"] = ["compiler"]
            verify(build, Catalog(modules), ["gen", "app"])
