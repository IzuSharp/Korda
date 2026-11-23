"""Match GitHub's PR metadata, the merged tree and the retained release candidate."""

from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "eng/ci"))
import promote

REPO = "IzuSharp/Korda"
HEAD = "a" * 40
MERGE = "b" * 40
TREE = "c" * 40
TEST_MERGE = "d" * 40
PR = dict(
    number=29,
    merged_at="2026-11-23",
    merge_commit_sha=MERGE,
    base={"ref": "main"},
    head={"sha": HEAD, "repo": {"full_name": REPO}},
)
WORKFLOW = {
    "ref": "refs/pull/29/merge",
    "path": f"{REPO}/.github/workflows/build.yml@{TEST_MERGE}",
    "sha": TEST_MERGE,
}
RUN = dict(
    id=123,
    run_number=186,
    run_attempt=1,
    conclusion="success",
    event="pull_request",
    head_sha=HEAD,
    head_repository={"full_name": REPO},
    pull_requests=[],
    referenced_workflows=[WORKFLOW],
)
PLAN = {
    "inputs": {"sqlite": "package-input"},
    "published": {"sqlite": {"version": "0.1.0-preview.163.2"}},
    "candidates": ["sqlite"],
    "releases": [{"suffix": ""}],
}


class PromotionTests(unittest.TestCase):
    def test_explicit_and_retained_pr_links(self):
        self.assertTrue(promote.matches_pr(RUN, PR, REPO))
        self.assertTrue(
            promote.matches_pr(
                RUN | {"pull_requests": [{"number": 29}], "referenced_workflows": []}, PR, REPO
            )
        )
        for changes in (
            {"pull_requests": [{"number": 2}]},
            {"head_repository": None},
            {"head_repository": {"full_name": "a/fork"}},
            {"head_sha": "0" * 40},
            {"event": "workflow_dispatch"},
            {"conclusion": "failure"},
            {"referenced_workflows": []},
            {"referenced_workflows": [WORKFLOW | {"ref": "refs/pull/290/merge"}]},
            {
                "referenced_workflows": [
                    WORKFLOW | {"path": f"another/repo/.github/workflows/build.yml@{TEST_MERGE}"}
                ]
            },
            {"referenced_workflows": [WORKFLOW | {"sha": "invalid"}]},
        ):
            with self.subTest(changes=changes):
                self.assertFalse(promote.matches_pr(RUN | changes, PR, REPO))

    def test_promotion_requires_exact_artifact_and_fresh_version(self):
        expected = f"providers-{TREE}-sqlite-123-1"
        for name, expired, version, eligible in (
            (expected, False, "0.1.0-preview.163.2", True),
            (expected, True, "0.1.0-preview.163.2", False),
            (expected.replace(TREE, MERGE), False, "0.1.0-preview.163.2", False),
            (expected, False, "0.1.0-preview.187.2", False),
        ):
            responses = [[PR], {"workflow_runs": [RUN]}, {"artifacts": [{"name": name, "expired": expired}]}]
            with (
                self.subTest(name=name, expired=expired, version=version),
                patch.object(promote, "api", side_effect=responses),
                patch.object(promote, "git", return_value=TREE.encode()),
            ):
                result = promote.promotion(
                    REPO, MERGE, PLAN | {"published": {"sqlite": {"version": version}}}
                )
                self.assertEqual(bool(result["run"]), eligible)

    def test_retry_requires_merged_main_history_and_current_package_inputs(self):
        with (
            patch.dict(promote.os.environ, {"GITHUB_REF": "refs/heads/main"}),
            patch.object(promote, "api", return_value=PR),
            patch.object(promote, "git") as git,
            patch.object(promote, "plan_tree", return_value=PLAN),
            patch.object(promote, "inputs", return_value={"sqlite": "updated"}),
            patch.object(promote, "promotion") as select,
        ):
            with self.assertRaisesRegex(ValueError, "inputs have changed"):
                promote.retry(REPO, "29")
            git.assert_called_once_with("merge-base", "--is-ancestor", MERGE, "HEAD")
            select.assert_not_called()
            with self.assertRaisesRegex(ValueError, "PR number"):
                promote.retry(REPO, "HEAD")
        with (
            patch.dict(promote.os.environ, {"GITHUB_REF": "refs/heads/feature"}),
            patch.object(promote, "api") as api,
        ):
            with self.assertRaisesRegex(ValueError, "main"):
                promote.retry(REPO, "29")
            api.assert_not_called()

    def test_retry_with_no_pending_packages_is_a_noop(self):
        with (
            patch.dict(promote.os.environ, {"GITHUB_REF": "refs/heads/main"}),
            patch.object(promote, "api", return_value=PR),
            patch.object(promote, "git"),
            patch.object(promote, "plan_tree", return_value={"candidates": []}),
            patch.object(promote, "promotion") as select,
        ):
            self.assertFalse(promote.retry(REPO, "29")["run"])
            select.assert_not_called()
