# Transaction consumers

`TransactionExamples.cs` contains minimal commit/dispose, uncertain-outcome recovery
with a bounded close, and savepoint recovery without committing the caller-owned
outer transaction. These functions accept the confirmed `DataTransaction` produced
by the owning B04 DataSession. No temporary source factory or fake public constructor
is introduced here.

The B05 component executable invokes these exact consumers against the product Rust
transaction coordinator through its test-only ABI in both JIT and Native AOT modes.
The full DataSession/provider package journey remains B04/B21/B24 composition.

The file builds as a standalone sample library with
`dotnet build samples/api/data-transactions/Data.Transactions.Sample.csproj`.
`python3 tests/Data.Transactions/verify.py` builds it and executes the consumers.
