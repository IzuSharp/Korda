"""Reject wrong artifacts with real ELF files and actual archive inspection."""
import importlib.util
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "src/Packaging.Linux" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


profile, packages = module("linux_profile"), module("packages")


class ProfileTests(unittest.TestCase):
    def test_real_elf_metadata_and_corruptions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "probe.c"
            source.write_text('#include <stdio.h>\nvoid nativeweave_probe(void) { puts("nativeweave"); }\n')
            native = root / "libprobe.so"
            subprocess.run(["cc", "-shared", "-fPIC", "-march=x86-64-v2", source, "-o", native], check=True)
            result = profile.inspect(native, allowed_dependencies=["libc.so.6"], expected_exports=["nativeweave_probe"])
            self.assertEqual(result["needed"], ["libc.so.6"])
            for change, code in [(dict(allowed_dependencies=[]), "NWPKG003"),
                                 (dict(expected_exports=[]), "NWPKG005"),
                                 (dict(maximum_glibc="2.0"), "NWPKG003")]:
                arguments = dict(allowed_dependencies=["libc.so.6"], expected_exports=["nativeweave_probe"])
                arguments.update(change)
                with self.assertRaisesRegex(profile.Rejected, code):
                    profile.inspect(native, **arguments)
            for label, offset, value in [("magic", 0, 0), ("class", 4, 1), ("endian", 5, 2), ("machine", 18, 183), ("kind", 16, 1)]:
                invalid = root / (label + ".so")
                data = bytearray(native.read_bytes())
                data[offset] = value
                invalid.write_bytes(data)
                with self.subTest(label=label), self.assertRaisesRegex(profile.Rejected, "NWPKG003"):
                    profile.header(invalid)

    def test_pack_exact_edges_identity_and_immutability(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first = packages.pack(root / "one", "NativeWeave.Test", "1.0.0", {"LICENSE": b"MIT"}, {"NativeWeave.Core": "1.0.0"})
            second = packages.pack(root / "two", "NativeWeave.Test", "1.0.0", {"LICENSE": b"MIT"}, {"NativeWeave.Core": "1.0.0"})
            self.assertEqual(first.read_bytes(), second.read_bytes())
            with zipfile.ZipFile(first) as archive:
                self.assertIn('version="[1.0.0]"', archive.read("NativeWeave.Test.nuspec").decode())
            with self.assertRaisesRegex(ValueError, "immutable"):
                packages.pack(root / "one", "NativeWeave.Test", "1.0.0", {"LICENSE": b"different"})
            with self.assertRaisesRegex(ValueError, "Noncanonical"):
                packages.pack(root / "invalid", "NativeWeave.Test", "1.0.0", {"../escape": b"invalid"})
            self.assertFalse(any((root / "invalid").iterdir()))


if __name__ == "__main__":
    unittest.main()
