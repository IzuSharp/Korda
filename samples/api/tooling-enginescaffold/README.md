# Minimal and advanced Engine scaffold usage

The minimal maintainer path is one synchronous real codec descriptor with `abi`,
`error`, `ownership` and `buffer`, its exact A09 input files, compiled API baseline,
typed facade and consumer source. Generate once into a new directory, then run its
installed `eng/verify.py` against the reviewed local package feed and pinned SDK.

The advanced profile uses the real A18 HTTP adapter and explicitly adds `async`
and `callback` with `EngineLocalAsync` threading. Its actual cancel/take/retirement
inventory and source coverage remain owned by A18; generation cannot synthesize
those obligations from a capability name. Both profile runs are generated from
their committed engine sources by `tests/Tooling.EngineScaffold/verify.py`.

Normal application consumers use the generated facade package and its C# sample.
They do not run this maintainer CLI, invoke Cargo, manage a callback cookie or
configure a global executor. Generated examples acquire product-executed status
only from their exact JIT/Native AOT receipt.
