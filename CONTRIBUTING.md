# Contributing

Read [AGENTS.md](AGENTS.md) for module registration, Native AOT validation and packaging rules. Use a topic branch and an isolated worktree. Write repository prose and code comments in English.

Keep public API changes together with XML documentation, compiled API checks and a realistic consumer example. Document resource ownership at implementation boundaries. The [API standard](docs/standards/README.md) records the existing database contract and its provenance.

Install the local hooks with `pre-commit install`. For database work, follow the [native build guide](docs/guides/cpp-native.md) and [database verification instructions](docs/verification.md). Use a disposable database server and run selected Native AOT consumers on Linux x64. Review lockfile changes whenever dependencies change.

Describe the behavior and tests in the PR. After merge the [release workflow](docs/releasing.md) can promote the verified top-level packages.

## Public API naming

Use `Korda.<Domain>` for packages and namespaces, such as `Korda.Database.Sqlite` and `Korda.Data`. Choose a domain that describes the capability developers use.

Name public attributes in PascalCase as **domain prefix + purpose + `Attribute`** in the type declaration. Use the short form in tutorials and business code: declare `DbTableAttribute`, write `[DbTable]`. Database attributes use the `Db` prefix, including `[DbCodec]` for a reusable conversion policy. The [database annotation guide](docs/guides/query-generator.md) documents their current behavior.

Prefixes describe public capabilities independently of the implementation language. A future QML integration could use `QmlPropertyAttribute` / `[QmlProperty]`; a future agent framework could use `AgentToolAttribute` / `[AgentTool]`. These QML and agent names are future examples, not available APIs. Public names should remain meaningful if the implementation language changes. Name ordinary business types and methods after their responsibilities.

When adding or changing a public name, update generator recognition and emitted code, API contracts, XML comments, documentation and compilable examples together. Regenerate API reference output from the updated sources and check links and anchors. Tutorials must use the current names so their examples can be copied directly into a consumer of the candidate package. Document source and binary compatibility changes in an upgrade guide and the PR.
