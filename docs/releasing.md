# Database previews

Inspect the committed diff with `python3 eng/ci/impact.py origin/main`. The Python planner checks every tracked path, expands module dependencies and lists the actions, runner resources and eligible packages. A new module registers its ownership and checks under [eng/ci/modules](../eng/ci/modules/README.md).

Documentation and CI orchestration changes run the tooling checks. An API snapshot runs the owning API check. A changed provider test builds its selected provider and executes on the Linux runner. Shared package inputs can affect both providers. PRs execute product consumers with Native AOT. C# and Python CodeQL follow the changed languages. Weekly native analysis runs C/C++ CodeQL and clang-tidy.

The public package allowlist contains **Korda.Database.Sqlite** and **Korda.Database.PostgreSql**. Each package includes the shared managed assemblies, build integration, generator, third-party notices and its selected provider library. Internal component packages and HelloWorld receive no new public versions.

Each candidate uses `0.1.0-preview.<CI run number>.2`. The PR verifies and retains the exact archive for the integration tree. A merge promotes a successful same-repository PR artifact with matching tree, package selection and eligible version. A missing or expired candidate stops publication. The trusted `release.yml` job verifies the allowlist, checks archive hashes before authentication, polls NuGet indexing every two minutes and executes the signed registry package with Native AOT. The combined consumer runs when both provider candidates are selected.

To retry a missed promotion after a CI fix has merged, select **Actions → Retry a merged preview → Run workflow** on `main` and enter the original PR number. This reuses its successful, unexpired candidate and checks the original merged tree and current package inputs before publishing. If the packages are already current, the retry exits without publishing.

GitHub Actions can restore caches from the base branch. PR caches remain scoped to their merge ref. The weekly or manual full run on main also warms the default-branch caches for subsequent PRs.

Manual CI with empty inputs runs full Linux validation. Set `check_paths` to tracked test or documentation files (one per line) to time the same check selection on a hosted runner; these replays are ineligible for promotion. Weekly CI runs compatibility and sanitizer checks on the host. For clean-image, CPU emulation, and runtime transfer checks, enable `verify_images` on a manual full run. The internal twelve-package feed remains available for historical compatibility work.
