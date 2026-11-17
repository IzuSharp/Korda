# Generated binding conformance sample

The sample compiles already generated imports, the static compiler metadata plugin,
the real A03 lifetime kernel and a labelled fixed D06 coordinator. It consumes a
precompiled A11 native asset. It does not expose a pretend domain Host.

```sh
dotnet run --project samples/api/generators-csharp -- /absolute/path/libnativeweave_interop_exports.so
```

The minimal route validates the A01 bootstrap through generated, handwritten and
direct imports, creates/reads/disposes a typed internal owner, and checks rejection
after Dispose. The advanced route uses static reverse callbacks and closed-generic
mapping, including notification before native start returns, bridge failure,
mapper exception and explicit final resource retirement.

```sh
dotnet publish samples/api/generators-csharp -c Release -r linux-x64 -p:PublishAot=true -o published
./published/NativeWeave.Generators.CSharp.Sample /absolute/path/libnativeweave_interop_exports.so --require-aot
```

No command above runs Cargo. Source/binary hashes and actual example execution
state are in the A10 Linux receipt. A16/B24 own pristine distribution; no runtime
NativeWeave package is added to the existing release workflow by this sample.
