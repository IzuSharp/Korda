# Managed contract reference bytes

These ten immutable fixtures were emitted by the accepted C# Data.Abstractions
consumer at NativeWeave commit `465cd0f90e6af05ec50eb581156240377cf0825b`.
The C# generator and public metadata/wire contracts were unchanged for DB-CPP-01.
`contracts_tests.cpp` ports the original Rust `interfaces/src/bin/verify-fixtures.rs`
assertions, including independent native scalar construction and 43 corruptions.

Generate fresh evidence outside this reference directory:

```sh
dotnet run --project tests/Data.Abstractions/NativeWeave.Data.Tests.csproj \
  -c Release -- /absolute/path/to/fresh-managed-fixtures
/path/to/contracts_tests /absolute/path/to/fresh-managed-fixtures
```

The generator passed all 12 managed cases without loading a native library.
`SHA256SUMS` records the exact reference payloads. `Program.cs` SHA-256:

`e46c54c488fde3ba17bc2d4625b6dad48321285806964cbd97e7c46352862595`
