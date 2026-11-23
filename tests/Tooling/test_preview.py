"""Exercise the production planner against committed trees and an isolated package registry."""

import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
from catalog import git, load_catalog
from facts import files, fingerprints
from impact import make_plan
from registry import next_preview, publication_state, public_heads


class PreviewTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        tree = git("write-tree").decode().strip()
        cls.catalog = load_catalog(tree)
        cls.entries = files(tree)
        cls.inputs = fingerprints(cls.catalog, cls.entries)

    def plan(self, paths, heads, main=True):
        with patch("impact.public_heads", return_value=heads) as registry:
            result = make_plan(self.catalog, paths, self.entries, self.catalog, reconcile=main)
        if main:
            registry.assert_called_once_with(self.catalog.packages)
        else:
            registry.assert_not_called()
        return result

    def test_docs_bootstrap_and_partial_delivery(self):
        docs = ["AGENTS.md"]
        missing = dict.fromkeys(self.catalog.packages, {"version": None, "inputs": None})
        plan = self.plan(docs, missing)
        self.assertEqual(plan["versions"], dict.fromkeys(self.catalog.packages, "0.1.0-preview.1"))
        self.assertEqual(set(plan["build_scopes"]), {"sqlite", "postgresql"})
        for p in plan["candidates"]:
            self.assertIn("public.validate", plan["actions"][p])
        current = {
            p: {"version": "0.1.0-preview.1", "inputs": fingerprint} for p, fingerprint in self.inputs.items()
        }
        partial = self.plan(docs, missing | {"sqlite": current["sqlite"]})
        self.assertEqual(partial["candidates"], ["postgresql"])
        self.assertEqual(partial["build_scopes"], ["postgresql"])
        self.assertEqual(self.plan(docs, current)["build_scopes"], [])
        self.assertEqual(self.plan(docs, missing, False)["build_scopes"], [])

    def test_selected_checks_and_independent_sequences(self):
        source = ["src/Data.Sqlite/SqliteOptions.cs"]
        heads = {
            "sqlite": {"version": "0.1.0-preview.4", "inputs": "old"},
            "postgresql": {
                "version": "0.1.0-preview.8",
                "inputs": self.inputs["postgresql"],
            },
        }
        main = self.plan(source, heads)
        self.assertEqual(main["candidates"], ["sqlite"])
        self.assertEqual(main["versions"]["sqlite"], "0.1.0-preview.5")
        self.assertEqual(main["actions"], self.plan(source, {}, False)["actions"])
        self.assertNotIn("pg-server", main["resources"]["sqlite"])
        tests = self.plan(["tests/Data.Sqlite/verify.py"], {}, False)
        self.assertEqual(tests["build_scopes"], ["sqlite"])
        self.assertFalse(tests["candidates"])
        self.assertFalse(tests["public_checks"])

    def test_registry_versions_and_embedded_fingerprint(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as z:
            z.writestr("korda/public.json", json.dumps({"inputs": "abc"}))
        index = json.dumps({"versions": ["0.1.0-preview.2", "0.1.0-preview.11", "0.2.0-preview.50"]}).encode()
        with patch("registry.read", side_effect=[index, stream.getvalue()]) as read:
            head = public_heads({"sqlite": "Korda.Database.Sqlite"})["sqlite"]
            self.assertEqual(head, {"version": "0.1.0-preview.11", "inputs": "abc"})
            self.assertIn("/0.1.0-preview.11/", read.call_args.args[0])
        self.assertEqual(next_preview(head["version"]), "0.1.0-preview.12")
        with patch("registry.read", side_effect=HTTPError("test", 503, "unavailable", {}, None)):
            with self.assertRaises(HTTPError):
                public_heads({"sqlite": "Korda.Database.Sqlite"})

    def test_recheck_before_publication_and_idempotent_retry(self):
        plan = {
            "inputs": {"sqlite": "desired"},
            "versions": {"sqlite": "0.1.0-preview.2"},
        }
        for latest, version, fingerprint, expected in (
            ("newer", None, None, (False, False)),
            ("desired", "0.1.0-preview.1", "old", (True, True)),
            ("desired", "0.1.0-preview.2", "desired", (False, True)),
        ):
            with (
                self.subTest(latest=latest, version=version),
                patch("facts.inputs", return_value={"sqlite": latest}),
                patch(
                    "registry.public_heads",
                    return_value={"sqlite": {"version": version, "inputs": fingerprint}},
                ),
            ):
                self.assertEqual(publication_state("sqlite", "HEAD", plan), expected)
        with (
            patch("facts.inputs", return_value=plan["inputs"]),
            patch(
                "registry.public_heads",
                return_value={"sqlite": {"version": "0.1.0-preview.3", "inputs": "other"}},
            ),
        ):
            with self.assertRaisesRegex(ValueError, "advanced"):
                publication_state("sqlite", "HEAD", plan)
