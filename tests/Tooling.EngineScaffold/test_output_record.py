"""Regression for SDK-selected build/publish paths; no device/Engine claim."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("scaffold_verifier", ROOT / "src/Tooling.EngineScaffold/templates/eng/verify.py")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


class OutputRecordTests(unittest.TestCase):
    def test_actual_sdk_build_and_publish_layouts(self):
        for relative in ("runtimes/linux-x64/native/libengine.so", "libengine.so"):
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                native = root / relative
                native.parent.mkdir(parents=True, exist_ok=True)
                native.write_bytes(b"record-layout-fixture-not-a-native-engine")
                digest = hashlib.sha256(native.read_bytes()).hexdigest()
                descriptor = {"engine_id": "test.engine", "package": {"id": "Test.Engine", "version": "1.0.0"},
                              "native": {"rid": "linux-x64", "filename": "libengine.so", "asset": {"sha256": digest}}}
                record = {"schemaVersion": 1, "engineId": "test.engine", "packageId": "Test.Engine",
                          "packageVersion": "1.0.0", "rid": "linux-x64", "sourcePath": "runtimes/linux-x64/native/libengine.so",
                          "outputPath": relative, "sha256": digest}
                path = root / "NativeWeave.test.engine.assets.json"
                path.write_text(json.dumps(record))
                self.assertEqual(verifier.native_output(root, descriptor), native)
                native.write_bytes(b"changed")
                with self.assertRaisesRegex(ValueError, "absent, changed"):
                    verifier.native_output(root, descriptor)

    def test_wrong_record_identity_and_path_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            descriptor = {"engine_id": "test.engine", "package": {"id": "Test.Engine", "version": "1.0.0"},
                          "native": {"rid": "linux-x64", "filename": "libengine.so", "asset": {"sha256": "0" * 64}}}
            record = {"schemaVersion": 1, "engineId": "test.engine", "packageId": "Test.Engine",
                      "packageVersion": "1.0.0", "rid": "linux-x64", "sourcePath": "runtimes/linux-x64/native/libengine.so",
                      "outputPath": "../libengine.so", "sha256": "0" * 64}
            path = root / "NativeWeave.test.engine.assets.json"
            path.write_text(json.dumps(record))
            with self.assertRaisesRegex(ValueError, "not canonical"):
                verifier.native_output(root, descriptor)
            record["packageVersion"] = "2.0.0"
            path.write_text(json.dumps(record))
            with self.assertRaisesRegex(ValueError, "differs from the descriptor"):
                verifier.native_output(root, descriptor)


if __name__ == "__main__":
    unittest.main()
