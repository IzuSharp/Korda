# Order/customer/detail Native AOT consumer

This is the complete example behind the [short integration tutorial](../../../docs/getting-started.md).
`Models.cs` contains ordinary classes/records, shared custom-ID/money/JSON codecs
and immutable DTOs. `Program.cs` uses generated descriptors with the existing
Fluent, SQL, write and administration APIs.

The acceptance driver copies these files outside the Korda source tree and
references **one** standalone provider package. It executes real database checks,
bootstraps customer and composite-key order scaffolds, compiles them into a
baseline, then modifies only the DTO source and verifies that both compared AOT
builds load the same packaged native image.
The edited run also sets `DtoEdit=true` to select the matching managed projection
and SQL branches in `Program.cs`. The native package and library remain identical.

## Run one provider

Copy `Models.cs`, `Program.cs`, `VerificationDatabase.cs` and
`OrmExperience.csproj` into an empty directory outside the repository. Use the
exact package version from a published preview or candidate `release.json`:

```sh
dotnet publish -c Release -r linux-x64 -p:Provider=Sqlite \
  -p:KordaVersion="$VERSION" -o out
./out/OrmExperience
```

For PostgreSQL, set `NW_TEST_POSTGRES` to an explicit local test URI and use
`-p:Provider=PostgreSql`. The test role needs `CREATEDB`: this fixture creates and
drops its own uniquely named database. It deliberately disables TLS for that
local test server. Application connection policy belongs in application
configuration; do not copy this isolation setup into normal startup.

The program refuses JIT execution. An optional `DB_API_SCAFFOLD` directory receives
`CustomerCopy.cs` and `OrderCopy.cs`. A successful process prints one `DB_API_RESULT` JSON object;
failed business assertions terminate the process.

## Maintainer acceptance

The existing per-provider workflow calls:

```sh
python3 eng/ci/consume.py artifacts/public/release.json artifacts/plan.json
# The same exact version is consumed from nuget.org after publication:
python3 eng/ci/consume.py artifacts/public/release.json artifacts/plan.json --public
```

The driver records page boundaries, nullable/empty associations, the actual SQL
execution and protocol submission deltas, same-DTO SQL results, grouped results,
partial update semantics, concurrency, callback commit/rollback/cancellation,
reviewed migration generation, compiled reverse mapping, and native hashes.
See [the contract and limits](../../../docs/guides/orm-experience.md).
