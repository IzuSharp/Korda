# Pinned API standard

`csharp-public-api-and-documentation.md` is a byte-for-byte snapshot pinned by the root `public-api-standard.json`. Its relative links refer to the upstream Design repository; read the [original at the exact commit](https://github.com/IzuSharp/Korda-Design/blob/c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9/docs/standards/csharp-public-api-and-documentation.md) for working cross-references. Preserving these exact bytes allows the local verification command to detect unreviewed changes.

The [project background](../background.md) describes the current scope. The [database verification guide](../verification.md) describes the checks applied to the pinned contract.

Current package, namespace and attribute spelling follows the [public API naming conventions](../../CONTRIBUTING.md#public-api-naming). Historical names in the pinned snapshot document its original adoption; current database examples use the [Db annotations](../guides/query-generator.md).

The Design repository is now named `Korda-Design`. Frozen `design-inputs.json` records retain their original `NativeWeave-Design:` input keys and source labels; their commit IDs, paths and content hashes are unchanged. Use the current repository URL when retrieving those commits.
