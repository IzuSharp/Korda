# Contributing

Read [AGENTS.md](AGENTS.md) for module registration, Native AOT validation and packaging rules. Use a topic branch and an isolated worktree. Write repository prose and code comments in English.

Keep public API changes together with XML documentation, compiled API checks and a realistic consumer example. Document resource ownership at implementation boundaries. The [API standard](docs/standards/README.md) records the existing database contract and its provenance.

Install the local hooks with `pre-commit install`. For database work, follow the [native build guide](docs/guides/cpp-native.md) and [database verification instructions](docs/verification.md). Use a disposable database server and run selected Native AOT consumers on Linux x64. Review lockfile changes whenever dependencies change.

Describe the behavior and tests in the PR. After merge the [release workflow](docs/releasing.md) can promote the verified top-level packages.
