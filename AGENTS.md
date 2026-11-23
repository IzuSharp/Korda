# Repository instructions

- Publish only top-level packages that users can install independently. Bundle each package's required internal components and native assets with it. Keep internal modules independently buildable and testable.
- For every code addition, move, change, or removal, check which components, tests, and public packages the CI planner selects. Use `python3 eng/ci/impact.py origin/main` to inspect the committed changes. Update the selection rules and their tests in the same PR only if they miss an affected component or select unrelated work.
- Select checks by dependency. Cover every consumer of changed shared code, run each required check once, and run independent checks in parallel. Register ownership for every new code path and test the routing before adding a runner requirement.
- Validate affected packages on the PR. After merge, publish only verified top-level packages with changed contents. Run affected product consumers with Native AOT directly on the Linux runner. Reserve clean-image and extended checks for scheduled or manual validation.
