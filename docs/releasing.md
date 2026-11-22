# Database candidate production and NuGet publication

DB-Scope-01 removed the old HelloWorld publisher. DB-CPP-01 then completed the C++ database migration and verified local candidates. The owner subsequently authorized public delivery of that accepted database product to the existing NuGet account `dekueon`, and withdrawal of every listed `NativeWeave.HelloWorld` version. These are separate acceptance events; the historical migration report does not claim that candidate production was public publication.

## Candidate production

`eng/build.py verify` requires a clean committed source, builds the independent SQLite/PostgreSQL native profiles, and calls `src/Packaging.Domain/prepare.py`. Preparation derives the twelve-package graph from the retained implementation and records source, lockfile, tool, package, native and license identities.

The previous/candidate pair uses immutable local versions. Shared packages keep the same bytes; the database facade, provider facades and native leaves advance together. Candidate feeds and evidence are under `artifacts/db-scope/prepared/`. Ordinary external consumers execute typed Fluent queries and DTO mappings against actual databases in JIT and Native AOT.

## Required verification

The default CI retains source/workspace/API/format/lock/security gates, real database regressions and mixed database failure oracles. It then runs shared Core closure/minimum CPU checks, database package negative cases, interrupted publication/resume, complete-closure rollback, real unsigned-signature rejection, a no-Rust SDK container and a runtime-only AOT transfer.

The local release coordinator in `src/Packaging.Release/release.py` writes an immutable local test feed and advertises it only after complete destination consumption. It does not silently turn a failed upload or incomplete consumer into success. Public promotion reuses the strict payload, closure and signature checks without changing the already accepted local receipts or their meaning.

## Public publication

`packaging/releases/DB-CPP-01-nuget.json` fixes product commit `3badc05be503991b5591732629dd7e5b0dbad111`, its successful main CI run, the exact candidate artifact ID/digest, and all twelve original package versions/hashes. The source artifact's normal database closure is promoted unchanged. The separate `.clean.*` test set is excluded. There is one database product: consumers install one of the two provider entry packages, with the required components restored transitively.

`.github/workflows/release.yml` uses the owner's existing `nuget` environment, `NUGET_PUBLISH_ENABLED=true` switch and `NuGet/login` trusted publisher for `dekueon`. The read-only PR job validates the identity and rejection controls. Publishing runs on main when the reviewed release manifest is merged, or on an explicit main-branch workflow dispatch to resume the same immutable release. Ordinary build CI never obtains NuGet credentials. No browser login or new long-lived secret is needed.

The publisher checks the original successful run and artifact, uploads packages in dependency order to NuGet.org, and downloads each exact version. A repository signature changes the enclosing ZIP, so verification compares every unsigned entry to the accepted archive and separately verifies the real NuGet repository signature, service index, owner and trusted certificate. Existing versions are accepted only if those checks prove the same payload. Different bytes at the same immutable version stop the release.

The independent public consumer uses the existing versioned C# business sample, fresh projects and empty caches outside the source tree, and only NuGet.org as a package source. It runs SQLite/PostgreSQL in JIT and Native AOT and checks restored package identities plus the actual loaded native library hashes. It never compiles the product native library. The accepted local-feed verifier remains unchanged.

HelloWorld retirement is recorded independently. NuGet.org's delete endpoint unlists a version; it preserves exact-version restore for existing applications. All versions are enumerated and their final `listed` state is checked. If the existing credential lacks unlist permission, that exact refusal remains an incomplete cleanup result, even when database uploads succeeded. No publisher scope or account permission is silently expanded.

Run the same reviewed entry points locally for read-only preparation or with an already authorized temporary credential in the trusted publisher job:

```sh
python3 tests/Packaging.Domain/test_nuget_publication.py
python3 tests/Packaging.Domain/test_nuget_public_consumer.py
python3 eng/publish_nuget.py preflight --manifest packaging/releases/DB-CPP-01-nuget.json --work-dir artifacts/nuget-publication
python3 eng/publish_nuget.py unlist-helloworld --manifest packaging/releases/DB-CPP-01-nuget.json --work-dir artifacts/nuget-publication
python3 eng/publish_nuget.py publish --manifest packaging/releases/DB-CPP-01-nuget.json --work-dir artifacts/nuget-publication
python3 eng/verify_nuget_public.py --release-record artifacts/nuget-publication/source/db-scope/prepared/candidate/release.json --signature-policy artifacts/nuget-publication/signature-policy.json --expected-source 3badc05be503991b5591732629dd7e5b0dbad111 --output artifacts/nuget-publication/consumer
```

`preflight` needs read access to the original GitHub artifact. Only the two registry mutation commands consume `NUGET_API_KEY`; it is neither printed nor retained. Public consumption needs .NET11, ordinary Linux Native AOT linker prerequisites and an isolated PostgreSQL18 server selected by `NW_TEST_POSTGRES`. A successful publication requires all package readbacks, all four consumer cells and HelloWorld retirement; partial results remain explicit. Actual receipts and observed PR/run links are recorded in the Plan publication follow-up.

Final current batch PRs, source commits and observed CI runs are recorded in the Plan handoff.
