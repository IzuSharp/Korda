# Database preview releases

A pull request runs the Linux package, database, Native AOT and security checks. After it merges, the main CI run builds once and calls `release.yml` after Verify succeeds. The publishing job uses the `nuget` environment and the existing NuGet trusted publisher. Direct pushes, PR builds and manual CI runs do not publish.

CI publishes the twelve IDs in the verified database dependency graph from that run's candidate feed. Package versions use `0.1.0-preview.<CI run number>` followed by the producer's existing suffix: `.1` for the seven shared packages and `.2` for the database facade, two providers and two native leaves. Applications install the provider; NuGet resolves its exact dependencies. Historical HelloWorld versions remain on nuget.org.

The job checks each uploaded package against the candidate and verifies its NuGet repository signature. It polls for indexing every two minutes and runs the SQLite and PostgreSQL examples from nuget.org in JIT and Native AOT. If publication stops after some packages are pushed, rerun the failed job of the same CI run to reuse its original candidate. An existing version must match the verified payload.

For a local candidate, run `python3 eng/build.py verify` with the [native build prerequisites](guides/cpp-native.md). The package feed is written to `artifacts/db-scope/prepared/candidate/feed`.
