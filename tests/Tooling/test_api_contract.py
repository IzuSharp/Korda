"""The additive contract cannot replace old members or lose new ones unnoticed."""

import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
import api_contract


class AdditiveApiTests(unittest.TestCase):
    def test_historical_member_cannot_be_overridden(self):
        old = {"M:Korda.Data.Table.Key": {"signature": "old"}}
        with self.assertRaisesRegex(ValueError, "shadows a frozen API"):
            api_contract.merge(old, {"Korda.Data": [{"id": next(iter(old)), "signature": "changed"}]})

    def test_missing_new_member_is_rejected(self):
        additions = {"Korda.Data": [{"id": "T:Korda.Data.Example", "signature": "public class Example"}]}
        with self.assertRaisesRegex(ValueError, "missing or changed"):
            api_contract.validate_additions(additions, "Korda.Data", [])

    def test_changed_new_signature_is_rejected(self):
        additions = {"Korda.Data": [{"id": "M:Korda.Data.Example.Map", "signature": "public string? Map()"}]}
        with self.assertRaisesRegex(ValueError, "missing or changed"):
            api_contract.validate_additions(additions, "Korda.Data", [{"id": "M:Korda.Data.Example.Map", "signature": "public string Map()"}])


if __name__ == "__main__":
    unittest.main()
