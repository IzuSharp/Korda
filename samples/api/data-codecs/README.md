# Data codec consumer

```sh
dotnet run --project samples/api/data-codecs
```

This sample exercises real canonical `NativeWeave.Data` typed snapshots,
configured decimal, typed NULL and static JSON metadata. B07's actual native
provider conversion is verified by `tests/Data.Codecs/verify.py`; the source/
session host remains a separate B04 composition. No Rust toolchain is invoked
by the managed sample.

Evidence state: source is included in the B07 component verification; executed
states and exact source/executable hashes are in the corresponding verifier run.
Do not describe this snapshot-only sample as a completed database application.
