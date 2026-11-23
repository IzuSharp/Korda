"""Round-trip a new package registration without an external registry or any credentials."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
import module_job
from impact import plan_tree
from validate_public import validate


@unittest.skipUnless(hasattr(hashlib, "file_digest"), "The existing package verifier requires Python 3.11+")
class PackageTests(unittest.TestCase):
    def test_candidate_packing_consumption_and_validation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifests = root / "eng/ci/modules"
            manifests.mkdir(parents=True)
            (root / ".gitignore").write_text("artifacts/\n")
            (root / "pack.py").write_text(
                "import json, os, zipfile\nfrom pathlib import Path\n"
                "env=os.environ\nid=env['NW_PACKAGE']\nv=env['NW_VERSION']\n"
                "with zipfile.ZipFile(Path(env['NW_FEED']) / f'{id}.{v}.nupkg', 'w') as z:\n"
                " z.writestr(id+'.nuspec', f'<package><metadata><id>{id}</id><version>{v}</version></metadata></package>')\n"
                " z.writestr('nativeweave/public.json',json.dumps({'inputs': env['NW_INPUTS']}))\n")
            (root / "consume.py").write_text(
                "from pathlib import Path\nimport os,zipfile\nf=next(Path(os.environ['NW_FEED']).glob('*.nupkg'))\n"
                "with zipfile.ZipFile(f) as z: assert 'nativeweave/public.json' in z.namelist()\n"
                "Path('artifacts/consumed').touch()\n")
            (manifests / "product.json").write_text(json.dumps({
                "ci": {"tests": ["eng/ci/", ".gitignore"]},
                "widget": {"sources": ["pack.py"], "tests": ["consume.py"], "package": "NativeWeave.Widget",
                           "pack": [sys.executable, "pack.py"], "consume": [sys.executable, "consume.py"]}}))
            def git(*args):
                return subprocess.check_output(["git", "-C", temp, *args], text=True).strip()
            def commit():
                git("add", ".")
                git("-c", "user.name=CI", "-c", "user.email=ci@example.invalid", "commit", "-qm", "fixture")
            git("init", "-q")
            commit()
            base = git("rev-parse", "HEAD")
            with (root / "pack.py").open("a") as stream:
                stream.write("# next candidate\n")
            commit()
            out = root / "artifacts/public"
            with patch("catalog.ROOT", root), patch.object(module_job, "ROOT", root), patch.object(module_job, "OUT", out):
                with patch("impact.public_heads", return_value={"widget": {"version": None, "inputs": None}}):
                    plan = plan_tree(base)
                self.assertEqual(([], ["widget"], ["widget"]), (plan["packages"], plan["candidates"], plan["module_packages"]))
                version = "0.1.0-preview.1.2"
                module_job.execute("pack", "widget", plan, version)
                self.assertTrue((root / "artifacts/consumed").exists())
                package = validate(out, plan, version, "widget")["NativeWeave.Widget"]
                with package.open("ab") as stream:
                    stream.write(b"changed")
                with self.assertRaisesRegex(ValueError, "Candidate changed"):
                    validate(out, plan, version, "widget")
