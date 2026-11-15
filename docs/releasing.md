# NuGet release setup

The repository builds and tests candidates without a NuGet login. Maintainers have authorized automatic preview publication when a PR is merged into `main`, after all release gates pass. Direct branch pushes do not publish. The distributable is **NativeWeave.HelloWorld**, an experimental Linux x64 package. macOS packages are local development artifacts until a separate release profile is approved and tested.

## One-time account setup (maintainer interaction)

1. Sign in to [nuget.org](https://www.nuget.org/) and confirm the account email. Choose the individual or organization that will own `NativeWeave.HelloWorld`; GitHub organization membership does not automatically create a NuGet organization.
2. Under **Trusted Publishing**, create a GitHub policy with:

   | Field | Value |
   | --- | --- |
   | Repository owner | `IzuSharp` |
   | Repository | `NativeWeave` |
   | Workflow file | `release.yml` (filename only) |
   | Environment | `nuget` |
   | Package glob | `NativeWeave.*` |
   | Scopes | Push new packages and new versions |
   | Policy owner | The intended NuGet package owner |

3. In GitHub create the `nuget` environment and choose the desired reviewers/ref restrictions. Add repository variable `NUGET_USER` with the NuGet login/profile name (not email). Set repository variable `NUGET_PUBLISH_ENABLED=true` **only when ready for actual public publication**. Before then, a missing/false variable keeps publication disabled. Do not paste tokens into this repository or chat.
4. Dependency graph and Dependabot alerts were enabled for this repository to run the required dependency-review gate. Configure private vulnerability reporting, push protection and required `Verify` checks as appropriate; those other settings have not been changed by this bootstrap.

The workflow uses `NuGet/login` with `id-token: write` and receives a short-lived credential. A long-lived `NUGET_API_KEY` secret is not required for this OIDC path. See the official [Trusted Publishing documentation](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

**A publishing glob and a reserved prefix are different.** `NativeWeave.*` limits the publishing policy; it does not reserve that namespace. For prefix protection, separately apply through NuGet's [prefix reservation process](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation). Availability/approval is not assumed. If a one-time manual API key is needed for account bootstrapping, scope it to `NativeWeave.*` and only the required push operations, expire/revoke it promptly, and keep it outside source. The committed workflow intentionally uses OIDC.

## Automatic preview versions

Normal development needs no manual version edit, tag or **Run workflow** click. Push changes to a topic branch, open a PR and merge it into `main` following the repository's review rules. Only the completed merge triggers automatic publication; direct pushes to `main`, topic branch pushes, open PRs and PRs closed without merging do not publish.

NuGet rejects `pull_request_target` publishing events. Release therefore listens for updates to `main` and uses GitHub's commit-to-PR API to require an already merged PR whose `merge_commit_sha` equals that exact commit and whose base is `main` in this repository. A direct push without that evidence exits successfully with publication skipped, before building a release candidate or obtaining credentials. API failures stop the release instead of assuming a merge. Open PRs and unmerged closures cannot qualify.

The source commit must also belong to `main`. The same accepted commit is passed to the build, security and publishing jobs, including queued runs and retries. The workflow never builds an unmerged PR head with publishing permissions, and merged contributions from forks use the same checks.

Release allocates `<VersionPrefix>-preview.<GITHUB_RUN_NUMBER>.<GITHUB_RUN_ATTEMPT>` once before building, with `VersionPrefix` read from `Directory.Build.props` (currently `0.1.0`). For example:

| Event | Example version |
| --- | --- |
| Release run 148, first attempt | `0.1.0-preview.148.1` |
| Next Release run | `0.1.0-preview.149.1` |
| **Re-run all jobs** for run 149 | `0.1.0-preview.149.2` |

The counter belongs to the **Release** workflow, not the CI workflow or NuGet. Failed runs, skipped direct pushes and manual rehearsals also consume run numbers, so published versions can have gaps. The workflow does not commit version bumps or create tags. Local builds retain their default `0.1.0-preview.1` unless a version is passed explicitly.

The numbering follows the approach in [DesktopPlatform's publisher](https://github.com/ArcForges/DesktopPlatform/blob/main/.github/workflows/publish-nuget.yml), using `preview` for this experimental package. See GitHub's [run number and attempt definitions](https://docs.github.com/en/actions/reference/workflows-and-actions/variables). Releases are serialized with [`queue: max`](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#concurrency), which retains up to 100 pending runs instead of replacing the previous pending run.

## Candidate and publication flow

1. Each PR merged into `main` starts **Release** and allocates its automatic preview version. Both `NUGET_PUBLISH_ENABLED=true` and the existing NuGet publisher configuration are still required; leave these configured for ongoing automatic publication.
2. The candidate job runs the full source/package/AOT loop and a Rust-free SDK container consumer. Security checks must also succeed. The publishing job downloads the exact artifact, checks SHA-256/native/source identities, exchanges OIDC credentials and uploads that package without rebuilding it.
3. After upload, it immediately checks the NuGet package download endpoint. If NuGet returns 404 while indexing, it checks again every **120 seconds**, for up to **20 minutes**. This is a bounded wait within the release job, not a recurring scheduled workflow. Other HTTP errors fail immediately. The job has a 40-minute budget including setup and consumption.
4. Once available, it verifies the NuGet signature, compares all unsigned payload entries against the candidate, then restores those registry bytes into a fresh cache and executes both JIT and AOT consumers. `publication.json` records `uploaded-awaiting-verification` immediately after upload and becomes `published-and-consumed` only after these checks pass. Upload and registry digests differ when repository signing changes archive bytes.

To rehearse without uploading, run **Release** manually on `main` and leave **publish** unchecked. Leave **version** empty for an automatic preview, or supply a canonical explicit version. Checking **publish** performs a real release after verification. Version tags such as `v0.2.0-preview.1` still publish the exact tagged version from a commit on `main`. Automatic merge releases never choose a stable version; stable-release policy will be decided separately.

## Retries and maintainer intervention

- **Re-run failed jobs** retains the original allocated version and uses the candidate output selected by the build job. The publisher does not derive a new version from its own attempt. This is suitable when failure occurred before upload.
- If upload succeeded but indexing or later verification failed, the version may already be public. Inspect `publication.json`, the logs and NuGet before retrying. Re-running only the publisher cannot overwrite that version. For a new automatic preview, use **Re-run all jobs** or a new manual Release with an empty version; this builds and tests a new candidate with a new version.
- Explicit manual versions and tags do not change on retry. Use a new version when a package with the previous version already exists.
- NuGet versions are immutable: there is no automatic overwrite or `--skip-duplicate` success assumption. Do not advertise a release as verified until registry consumption succeeds.
- Missing/expired publisher configuration, a failed gate or environment approval rules can still require maintainer action. Set `NUGET_PUBLISH_ENABLED=false` to stop publication while retaining candidate checks.

CI and Release upload packages, manifests and logs as run artifacts; they do not create a GitHub Release or publish documentation automatically. Source/manifest checksums are consistency evidence, not an author-signing certificate or a completed supply-chain attestation policy. Production engines require the fuller Design release obligations.
