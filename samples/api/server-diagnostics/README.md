# Explicit Server diagnostics

This finite consumer uses the real native Host and public .NET interfaces. It runs the same requests with diagnostics disabled or explicitly enabled; `Debug` does not select the latter automatically.

```sh
dotnet run --project samples/api/server-diagnostics
dotnet run --project samples/api/server-diagnostics -- --local
```

The local mode installs one application-owned ActivityListener (one request in eight), one MeterListener, and a Warning/Error console provider limited to 32 messages and 256 characters per message. The framework's separate metadata queue also retains its own finite entry/byte budgets. No network exporter is installed. Disposal joins business resources before the independent finite diagnostic flush.

The project can also be used as a NuGet consumer by setting `NativeWeaveDiagnosticsPackageVersion` to an available package version and supplying its package feed. Consumer build/run does not invoke Cargo. Maintainer source builds first supply the normal precompiled `NativeWeave.Server` asset; see the [diagnostics guide](../../../docs/guides/server-diagnostics.md).
