# MCP consumer

Install the maintainer's Linux x64 package feed, restore this standalone project with
the repository's pinned .NET 11 SDK, and run:

```sh
dotnet run -- --self-test --minimal
dotnet run -- --self-test
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
./bin/Release/net11.0/linux-x64/publish/Mcp.Sample --self-test
```

Minimal mode maps one static typed tool. Advanced mode adds endpoint authorization,
a fixed text resource and a declared string-argument prompt. The self-test performs
actual HTTP/1.1 and HTTP/2 calls and checks final native/managed ownership counters.
Running without `--self-test` listens on loopback port5000 until shutdown. The advanced
example uses `X-Demo-Caller: local` only to demonstrate the authenticator seam.

The only package reference is `NativeWeave.Server.Mcp`; its normal transitive closure
contains the Server facade and the maintainer-precompiled native module. There is no
ProjectReference, direct native path, runtime compiler or consumer Rust command.

See [the guide](../../../docs/guides/protocols-mcp.md) for the exact modern MCP metadata,
Host/Origin policy, bounded JSON profile, cancellation and disposal rules. Changing
`AddInput`, `AddOutput` or a handler rebuilds C# while preserving the native asset.
