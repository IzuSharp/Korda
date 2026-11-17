# Native Server consumers

The minimal consumer starts the real native HTTP listener, maps a static handler, makes one loopback request and awaits disposal. The advanced consumer adds bounded duplex echo and SSE through the native Axum formatter.

```sh
dotnet run --project samples/api/server-native/server-native.csproj
dotnet run --project samples/api/server-native/server-native.csproj -- --advanced
```

Repository verification references the actual product project and copies its maintainer-built native asset. Application consumers replace that project reference with the `NativeWeave.Server` NuGet package; ordinary handler changes use .NET only. B17's verifier also runs an explicit `linux-x64` NativeAOT static dispatcher. A16/B24 own the pristine package/no-Rust consumption matrix.

See `docs/guides/server-native.md` for startup, protocols, TLS reload, cancellation and retained-owner shutdown behavior. The advanced result callback runs within the request scope; it cannot retain a borrowed response stream after completion. Dispose every retained BodyChunk separately.
