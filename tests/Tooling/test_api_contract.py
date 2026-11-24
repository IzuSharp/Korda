"""The additive contract cannot replace old members or lose new ones unnoticed."""

import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
import api_contract


class AttributeRenameTests(unittest.TestCase):
    def test_reviewed_names_preserve_the_rest_of_each_frozen_contract(self):
        renames = api_contract.naming_migration(Path(__file__).resolve().parents[2])
        for old, current in renames.items():
            with self.subTest(attribute=current):
                ident = "T:Korda.Data." + old
                signature = (
                    "[System.AttributeUsageAttribute(System.AttributeTargets.Property, Inherited = false)]\npublic sealed class Korda.Data."
                    + old
                    + " : System.Attribute"
                )
                baseline = {
                    ident: {
                        "id": ident,
                        "signature": signature,
                        "parameters": [{"name": "name", "default": None}],
                        "source": "https://github.com/IzuSharp/NativeWeave-Design",
                        "implicit_member": False,
                    }
                }
                projected = api_contract.rename_attributes(baseline, renames)
                expected = dict(
                    baseline[ident], id="T:Korda.Data." + current, signature=signature.replace(old, current)
                )
                self.assertEqual(projected, {expected["id"]: expected})
                self.assertEqual(baseline[ident]["id"], ident)
                self.assertEqual(baseline[ident]["signature"], signature)

    def test_rename_cannot_hide_a_collision_with_an_existing_public_type(self):
        renames = api_contract.naming_migration(Path(__file__).resolve().parents[2])
        old, current = next(iter(renames.items()))
        baseline = {
            "T:Korda.Data." + name: {"id": "T:Korda.Data." + name, "signature": name}
            for name in (old, current)
        }
        with self.assertRaisesRegex(ValueError, "rename collides"):
            api_contract.rename_attributes(baseline, renames)


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
            api_contract.validate_additions(
                additions,
                "Korda.Data",
                [{"id": "M:Korda.Data.Example.Map", "signature": "public string Map()"}],
            )


if __name__ == "__main__":
    unittest.main()
