"""Fail-closed descriptor checks; these are not real Engine conformance rows."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("engine_scaffold", ROOT / "src/Tooling.EngineScaffold/scaffold.py")
scaffold = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scaffold)


class DescriptorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        def source(name, content):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)
            return {"path": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        sidecar = {"schema_version": 1, "module": {"id": "nativeweave.scaffold.test", "abi_major": 1, "abi_minor": 0},
                   "capabilities": ["codec.test.v1"], "exports": [{"symbol": "nw_test_get_abi_v1"}], "owners": [], "operations": []}
        self.data = {"schema_version": 1, "engine_id": "nativeweave.scaffold.test",
            "package": {"id": "NativeWeave.Scaffold.Test", "assembly": "NativeWeave.Scaffold.Test",
                        "namespace": "NativeWeave.Scaffold.Test", "version": "0.1.0-proof.1"},
            "abi": {"major": 1, "minor": 0, "bootstrap": "nw_test_get_abi_v1", "error_domain": 1},
            "threading": "Synchronous", "components": ["abi", "error"],
            "capabilities": [{"name": "codec.test.v1", "bit": 0}],
            "native": {"asset": source("fixture.so", "validation-only fixture, never loaded"), "filename": "libfixture.so",
                       "rid": "linux-x64", "target": "x86_64-unknown-linux-gnu", "cpu": "x64-v2", "link_mode": "dynamic"},
            "binding": {"sidecar": source("binding.json", json.dumps(sidecar)), "inventory": source("inventory.json", "{}"),
                        "adapter": source("adapter.rs", ""), "linked_symbols": source("symbols.txt", "nw_test_get_abi_v1\n"), "library": "fixture"},
            "upstream": [{"repository": "https://github.com/example/fixture", "commit": "1" * 40,
                          "crate": "fixture", "version": "1.0.0", "checksum": "2" * 64, "features": [], "patches": [], "license": "MIT"}],
            "files": {"src/Engine/Facade.cs": source("Facade.cs", "internal static class Facade {}"),
                      "samples/Consumer/Program.cs": source("Program.cs", "System.Console.WriteLine(1);"),
                      "docs/public-api.expected.json": source("api.json", "[]"),
                      "docs/guide.md": source("guide.md", "fixture")},
            "dependencies": {"NativeWeave.Core": "0.1.0-proof.1"}, "coverage": source("coverage.json", "{}")}

    def tearDown(self):
        self.temp.cleanup()

    def read(self, data):
        path = self.root / "descriptor.json"
        path.write_text(json.dumps(data))
        return scaffold.load(self.root, path)

    def reject(self, change):
        value = copy.deepcopy(self.data)
        change(value)
        with self.assertRaises(scaffold.ScaffoldError):
            self.read(value)

    def test_valid_minimum_has_no_runtime(self):
        self.assertEqual(self.read(self.data)["components"], ["abi", "error"])

    def test_reject_unknown_schema_and_capability(self):
        self.reject(lambda d: d.update(schema_version=2))
        self.reject(lambda d: d["components"].append("unreviewed-runtime"))
        self.reject(lambda d: d["capabilities"].append({"name": "codec.test.v1", "bit": 1}))
        self.reject(lambda d: d["capabilities"][0].update(bit=64))

    def test_reject_missing_lifetime_and_unsupported_target(self):
        self.reject(lambda d: d.update(components=["abi", "async", "error"], threading="EngineLocalAsync"))
        self.reject(lambda d: d["native"].update(rid="win-x64"))
        self.reject(lambda d: d["native"].update(link_mode="static"))
        self.reject(lambda d: d.update(threading="Unknown"))

    def test_missing_owner_release_and_wrong_identity(self):
        for value in ({"kind": "output", "release": "not_exported"},):
            model = json.loads((self.root / "binding.json").read_text())
            model["owners"] = [value]
            (self.root / "binding.json").write_text(json.dumps(model))
            self.data["binding"]["sidecar"]["sha256"] = scaffold.digest(self.root / "binding.json")
            with self.assertRaises(scaffold.ScaffoldError): self.read(self.data)

    def test_reject_changed_bytes_and_path_escape(self):
        self.reject(lambda d: d["native"]["asset"].update(sha256="0" * 64))
        self.reject(lambda d: d["files"]["src/Engine/Facade.cs"].update(path="../outside.cs"))
        self.reject(lambda d: d["files"].update({"../outside.cs": d["files"]["src/Engine/Facade.cs"]}))
        outside = Path(self.temp.name).parent / "outside-scaffold-fixture"
        outside.write_text("outside")
        try:
            (self.root / "escape").symlink_to(outside)
            self.reject(lambda d: d["files"]["src/Engine/Facade.cs"].update(path="escape"))
        finally:
            outside.unlink()

    def test_required_api_and_consumer_inputs(self):
        self.reject(lambda d: d["files"].pop("docs/public-api.expected.json"))
        self.reject(lambda d: d["files"].pop("samples/Consumer/Program.cs"))
        self.reject(lambda d: d.update(upstream=[]))
        self.reject(lambda d: d["dependencies"].update({"NativeWeave.Core": "*"}))


if __name__ == "__main__":
    unittest.main()
