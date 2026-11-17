# Export component consumer

This source sample consumes the prebuilt A11 conformance component. It is not an
installable database/server package. The exact sample state and native identity
come from the A11 Linux receipt; source alone is not an executed-state claim.

Minimal path:

```sh
dotnet run --project samples/api/generators-rust -- /absolute/path/libnativeweave_interop_exports.so
```

Advanced explicit-RID path:

```sh
dotnet publish samples/api/generators-rust -c Release -r linux-x64 -p:PublishAot=true -o published
./published/NativeWeave.Generators.Rust.Sample /absolute/path/libnativeweave_interop_exports.so --require-aot
```

The common source intentionally checks success, failure-atomic outputs, explicit
creator release and exact ABI layout, with no Cargo invocation during consumption.
It keeps the native module loaded for process lifetime, as required by the v1 profile.
