# Repository instructions

- Publish only top-level packages that users can install independently. Bundle each package's required internal components and native assets with it. Keep internal modules independently buildable and testable.
- For every code addition, move, change, or removal, check which components, tests, and public packages the CI planner selects. Use `python3 eng/ci/impact.py origin/main` to inspect the committed changes. Update the selection rules and their tests in the same PR only if they miss an affected component or select unrelated work.
- Select checks by dependency. Cover every consumer of changed shared code, run each required check once, and run independent checks in parallel. Broaden validation when the impact is uncertain.
- Validate affected packages on the PR. After merge, publish only verified top-level packages with changed contents. Test-only and documentation-only changes outside package contents should run their relevant checks without triggering a release.
