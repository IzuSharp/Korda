"""Exercise build orchestration with disposable inputs and no native SDK."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng"))
sys.path.insert(0, str(ROOT / "eng/ci"))

import build
import catalog


class BuildInputsTests(unittest.TestCase):
    def test_workspace_uses_registered_database_membership(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifests = root / "eng/ci/modules"
            manifests.mkdir(parents=True)
            for family in ("database", "qt"):
                (manifests / (family + ".json")).write_text(
                    json.dumps({family: {"sources": [family + "/"], "checks": ["example.api"]}})
                )
            (root / "Korda.slnx").write_text(
                '<Solution><Folder Name="/Products/"><Project Path="database/Db.csproj"/>'
                '<Project Path="qt/Ui.csproj"/></Folder></Solution>'
            )
            output = root / "artifacts" / "database.slnx"
            with patch.object(build, "ROOT", root), patch.object(catalog, "ROOT", root):
                build.workspace(output)
            result = [(output.parent / p.attrib["Path"]).resolve() for p in ET.parse(output).iter("Project")]
            self.assertEqual(result, [(root / "database/Db.csproj").resolve()])

    def test_workspace_does_not_jit_build_runtime_consumers(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifests = root / "eng/ci/modules"
            manifests.mkdir(parents=True)
            (manifests / "database.json").write_text(json.dumps({"database": {
                "sources": ["src/"], "tests": ["samples/", "tests/"]}}))
            paths = ["src/Database/Db.csproj", "samples/orders/Orders.csproj",
                     "tests/Database/Database.Tests.csproj", "tests/Database/ApiCheck/ApiCheck.csproj",
                     "tests/Build/ApiFixture/ApiFixture.csproj"]
            (root / "Korda.slnx").write_text('<Solution><Folder Name="/All/">' +
                ''.join('<Project Path="' + path + '"/>' for path in paths) + '</Folder></Solution>')
            output = root / "artifacts/database.slnx"
            with patch.object(build, "ROOT", root), patch.object(catalog, "ROOT", root):
                build.workspace(output)
            result = {(output.parent / item.attrib["Path"]).resolve() for item in ET.parse(output).iter("Project")}
            self.assertEqual(result, {(root / paths[index]).resolve() for index in (0, 3, 4)})

    @unittest.skipUnless(sys.platform == "linux" and hasattr(hashlib, "file_digest"), "Linux cache contract")
    def test_postgres_cache_key_tracks_its_profile_compiler_and_dependency_inputs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            script = root / "eng/postgres-cache-key.py"
            script.parent.mkdir()
            shutil.copyfile(ROOT / "eng/postgres-cache-key.py", script)
            lock = root / "native/dependencies.lock.json"
            lock.parent.mkdir()
            lock.write_text(json.dumps({"dependencies": [{"name": "libpq", "version": "18"}]}))
            setup = root / "eng/postgres-tools.sh"
            patchfile = root / "native/cpp/patches/libpq-explicit-configuration.patch"
            patchfile.parent.mkdir(parents=True)
            cc = root / "clang"
            for path in (setup, patchfile, cc):
                path.write_text("input")
            pkg_config = root / "pkg-config"
            pkg_config.write_text("#!/bin/sh\necho pinned\n")
            pkg_config.chmod(0o755)
            env = os.environ | {"CC": str(cc), "PATH": str(root) + os.pathsep + os.environ["PATH"]}

            def key(profile="full"):
                return subprocess.check_output([sys.executable, script, profile], env=env, text=True).strip()

            baseline = key()
            self.assertEqual(len(baseline), 64)
            self.assertEqual(key(), baseline)
            self.assertNotEqual(key("client"), baseline)
            for path in (cc, patchfile, setup):
                with self.subTest(path=path.name):
                    path.write_text("different")
                    self.assertNotEqual(key(), baseline)
                    path.write_text("input")
            lock.write_text(json.dumps({"dependencies": [{"name": "libpq", "version": "19"}]}))
            self.assertNotEqual(key(), baseline)
