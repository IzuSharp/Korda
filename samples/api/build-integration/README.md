# A13 build integration consumer

Owner: A13. Scope: the real, explicitly labelled build-conformance native asset,
not a new Engine ABI or distributable application package. `Program.cs` exercises
the minimal version/value path and the advanced overflow-boundary value against
the same precompiled bytes. It has no Cargo/MSBuild generation dependency.

1. Run the reviewed `linux-x64.json` profile through `build_native.py build`.
2. Verify the returned manifest and make its one native directory available to the
   sample process. This fixture-only path is controlled by the verification runner.
3. Use normal `dotnet run --project samples/api/build-integration -c Release`.
4. Publish with `-r linux-x64 -p:PublishAot=true`, then execute the produced binary.

`tests/Build.Integration/verify.py --output artifacts/task03/A13/NEW_RUN` performs
the complete build-failure and JIT/AOT consumer path, records hashes and installs
Rust invocation traps for the managed steps. Exact source/command/tool/log/binary
identities are retained in Plan; source text alone does not establish execution.
Windows, a pristine no-Rust machine and actual package release composition remain
outside this Linux component result.
