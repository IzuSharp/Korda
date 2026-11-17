# Server streaming consumers

`StreamingExamples.cs` is compiled into the B16 component consumer and its minimal
and advanced owned-chunk paths are actually executed by `tests/Server.Streaming`.
The borrowed-stream path is exercised against the native allocation/operation
fixture. It creates no managed HTTP listener or parser.

The Server application supplies `Request.Body` or `ReadChunkAsync`; that full
HTTP application is B15/B17. B16 sources are composed into `NativeWeave.Server`
and do not introduce a separately distributed streaming package.

```sh
python3 tests/Server.Streaming/verify.py --offline
```

`BodyChunk.CopyTo` uses caller-owned memory. `ToArray` explicitly allocates and
copies after a bounded nonwaiting overlap grant, then transfers the array to
application ownership. Dispose every chunk. A retained chunk can outlive the
request while retaining its original allocation credit, but borrowed streams,
request metadata and request services cannot outlive dispatch.

The maintained verification script builds the native component first, compiles
this source with fixed .NET 11 / C# 15, and executes JIT and Linux x64 Native AOT.
No Windows or complete HTTP Host acceptance is implied by this component sample.
