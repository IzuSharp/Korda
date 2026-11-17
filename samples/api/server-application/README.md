# Server application consumers

This source uses only exact NuGet package references. It imports the endpoint generator transitively from `NativeWeave.Server`; no consumer Cargo or schema compilation is required. Use the maintainer's tested candidate feed or the corresponding published package feed.

`dotnet run -- --minimal` starts the minimal HTTP application on loopback port 5000. `dotnet run` enables generated route/query/service binding and statically typed JSON. `dotnet run -- --self-test` executes the advanced application over actual HTTP/1 and HTTP/2, using the optional Generic Host integration to start and stop the same Rust Host. Add `--minimal` to exercise the minimal application instead.

For Linux Native AOT, use `dotnet publish -c Release -r linux-x64 -p:PublishAot=true`, then execute the produced sample with `--self-test`. The B15 package verifier copies this unchanged public consumer outside the implementation project graph, stages exact packages through the existing A14 packer and selected-asset task, and runs JIT/AOT with failing Rust-tool traps.
