"""Non-executing signature mutations validate single-description propagation."""
from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("abi_generation", ROOT / "native/interop/abi/generate.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)
SCHEMA = json.loads(generator.SCHEMA.read_text())


class SignatureGenerationTests(unittest.TestCase):
    def test_callback_parameter_width_changes_all_declarations(self):
        changed = deepcopy(SCHEMA)
        changed["callbacks"][1]["parameters"][-1][1] = "u32"
        outputs = generator.generated(changed)
        self.assertIn("const void * payload, uint32_t payload_bytes", outputs["native/interop/abi/include/nativeweave_abi_v1.h"])
        self.assertIn("payload_bytes: u32", outputs["native/interop/abi/src/generated.rs"])
        self.assertIn("delegate* unmanaged[Cdecl]<nuint, uint, void*, uint, uint>", outputs["src/Interop.Abi/WireRecords.g.cs"])

    def test_callback_return_width_changes_all_declarations(self):
        changed = deepcopy(SCHEMA)
        changed["callbacks"][0]["return"] = "u64"
        outputs = generator.generated(changed)
        self.assertIn("typedef uint64_t (NW_CDECL *nw_on_terminal_fn)", outputs["native/interop/abi/include/nativeweave_abi_v1.h"])
        self.assertIn("borrowed_error: *const NwErrorInfoV1) -> u64", outputs["native/interop/abi/src/generated.rs"])
        self.assertIn("delegate* unmanaged[Cdecl]<nuint, uint, NwErrorInfoV1*, ulong>", outputs["src/Interop.Abi/WireRecords.g.cs"])

    def test_bootstrap_typedef_uses_description_parameters_and_return(self):
        changed = deepcopy(SCHEMA)
        changed["bootstrap"]["parameters"][1][1] = "u64"
        changed["bootstrap"]["return"] = "u64"
        header = generator.generated(changed)["native/interop/abi/include/nativeweave_abi_v1.h"]
        self.assertIn("typedef uint64_t (NW_CDECL *nw_get_abi_v1_fn)(uint32_t requested_major, uint64_t out_capacity, nw_abi_info_v1 * out_info);", header)

    def test_unsupported_language_boolean_cannot_enter_wire_signatures(self):
        changed = deepcopy(SCHEMA)
        changed["callbacks"][0]["parameters"][1][1] = "bool"
        with self.assertRaises(KeyError):
            generator.generated(changed)


if __name__ == "__main__":
    unittest.main(verbosity=2)
