# Independent domain package consumers

`Program.cs` is ordinary C# application source copied into a fresh SDK project by
`tests/Packaging.Domain/consumer_cases.py`. The controller selects exact versions
from the producer manifest; the sample never assumes a uniform transitive version
or invokes Cargo. These are local candidate packages, not a public NuGet release.

| Graph | Compilation symbols | Direct packages |
| --- | --- | --- |
| SQLite | `SQLITE` | `NativeWeave.Database.Sqlite` |
| PostgreSQL | `POSTGRES` | `NativeWeave.Database.PostgreSql` |
| Server | `SERVER` | `NativeWeave.Server` |
| gRPC only | `SERVER;GRPC` | `NativeWeave.Server.Grpc` |
| MCP only | `SERVER;MCP` | `NativeWeave.Server.Mcp` |
| Combined | `SQLITE;POSTGRES;SERVER;GRPC;MCP` | Both providers, gRPC and MCP |

For a minimal consumer check inside the existing pristine SDK image, use the
complete previous local closure in a new output directory:

```sh
python3 src/Packaging.Domain/consume.py \
  --manifest /prepared/manifest.json --closure previous \
  --feed /destination --output /results/previous-smoke --mode smoke
```

This runs normal restore/build/execution on the complete combined graph with
SQLite and the explicitly configured real PostgreSQL service. It is the same
callback used for local publication and fresh-process rollback.

The advanced candidate check adds independent graph isolation, actual AOT,
ordinary model/DTO/Handler edits and the focused assembled negatives:

```sh
python3 src/Packaging.Domain/consume.py \
  --manifest /prepared/manifest.json --closure candidate \
  --feed /destination --output /results/candidate-full --mode full
```

`AddedModels.cs.template` is copied as `AddedModels.cs` only **after** both
restores complete. The controller enables `MANAGED_EDIT`, empties restore
sources, rebuilds and executes normally, then publishes and executes Native
AOT. Its new typed table/Entity/DTO are exercised through the existing native
database and its new Handler through actual HTTP. Native identity is checked
against the pre-edit output, rather than inferred from a successful build.

The bounded protobuf codec and HTTP/2 gRPC observation follow the accepted
B22 ApiProbe; typed database metadata follows the existing ORM relation sample.
HTTP/TLS and MCP use the existing Server public APIs. There is no custom FFI,
managed server host or managed database-driver substitute. C# emits one terminal
`B24_RESULT` only after domain owners close successfully. `--require-aot` checks
the actual runtime witness; `--reject-abi` is an isolated negative entry that
never creates a domain owner.

See [the domain package guide](../../../docs/guides/packaging-domain.md) for the
exact source-mapping, closure/version rules, prerequisites, transfer interface,
and evidence/acceptance boundaries. An unexecuted example remains source-only;
successful historical component tests are not results for these new packages.
