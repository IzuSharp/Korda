# Independent package consumer

These templates become a real application outside the source tree when
`tests/Packaging.Release/consume.py` runs. Only normal NuGet `PackageReference`
items reach the consumer. The staged facade uses the actual immutable
`EnginePackageManifest`, Core loader and product ABI bootstrap; it is a local
conformance package, not a Database or Server implementation.

`Facade.cs.in` describes one independently versioned native leaf.
`Program.cs.in` inspects its native identity, exercises a missing-capability
diagnostic and runs typed Entity/DTO/Handler code. `Consumer.nwmetadata` gives the
transitive generator valid static application metadata without inventing native
imports for ordinary managed models. Its generated metadata and analyzer both
execute during compilation; their host assemblies are absent at runtime.

The harness adds a second Entity/DTO/Handler set after AOT restore, clears all
feeds, and runs offline build/publish with `--no-restore`. The original native
hash must remain identical. `--require-aot` also checks that dynamic code is
unavailable before the native package is accepted as an AOT execution.

From the repository root with the pinned tools installed:

```sh
python3 src/Packaging.Release/prepare.py --output artifacts/task04/A16/prepared
python3 tests/Packaging.Release/consume.py \
  --prepared artifacts/task04/A16/prepared \
  --output artifacts/task04/A16/consumer --negative-cases
```

The output includes actual restore/build/run/publish logs, lock and package
hashes, SDK native output records, a structured receipt and the transferable AOT
directory. The advanced cases remove the record, tamper with the native file,
request an absent capability and directly override Core with NuGet warnings
enabled and suppressed. Each fails at its intended boundary.

See the [packaging guide](../../../docs/guides/packaging-release.md) for exact
closure schema, clean-image acceptance, source provenance and immutable release
recovery. This sample does not upload packages or claim Windows acceptance.
