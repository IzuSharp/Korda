# Database previews

Inspect the committed diff with `python3 eng/ci/impact.py origin/main`. The Python planner checks every tracked path, expands module dependencies and lists the actions, runner resources and eligible packages. A new module registers its ownership and checks under [eng/ci/modules](../eng/ci/modules/README.md).

Documentation and CI orchestration changes run the tooling checks. An API snapshot runs the owning API check. A changed provider test builds its selected provider and executes on the Linux runner. Shared package inputs can affect both providers. PRs execute product consumers with Native AOT. C# and Python CodeQL follow the changed languages. Weekly native analysis runs C/C++ CodeQL and clang-tidy.

The public package allowlist contains **Korda.Database.Sqlite** and **Korda.Database.PostgreSql**. Each package includes the shared managed assemblies, build integration, generator, third-party notices and its selected provider library. The database release bundles internal components in those two packages. The NuGet trusted publisher for this repository grants the `Korda.*` prefix to `release.yml` with environment `nuget`.

PRs and main pushes verify the affected modules using the proposed merge diff or the push's before/after commits. Each selected provider builds on a separate Linux runner, creates a standalone package and runs its own Native AOT consumer. PR and scheduled checks use temporary versions.

On each main push the planner also compares every registered public ID's package-input fingerprint with its last indexed preview. A missing or changed package gets its own next version, starting at `0.1.0-preview.1`. An unchanged ID keeps its current version. Even a documentation merge can deliver a previously missing preview. Main validates the final version and retains one artifact per package. The required Verify job checks for each expected artifact before publication.

The protected release job checks the source tree, package ID, version and archive hashes before login. It rechecks the latest main inputs and NuGet state, polls indexing every two minutes, verifies the signed package and runs the registry consumer. A failed job can be rerun; a later main push also reconciles any outstanding package independently. Keep `NUGET_PUBLISH_ENABLED` enabled for automatic publication.

GitHub Actions can restore caches from the base branch. PR caches remain scoped to their merge ref. The weekly or manual full run on main also warms the default-branch caches for subsequent PRs.

Manual CI with empty inputs runs full Linux validation. Set `check_paths` to tracked test or documentation files (one per line) to time the same check selection on a hosted runner; these replays are ineligible for promotion. Weekly CI runs compatibility and sanitizer checks on the host. For clean-image, CPU emulation, and runtime transfer checks, enable `verify_images` on a manual full run. The internal twelve-package feed remains available for historical compatibility work.
