# Synchronous real-engine consumers

The sample exercises the local `NativeWeave.Engines.SyncProof` 0.1.0-proof.1 facade and shared Core buffer ownership on Linux x64. The default profile is the minimal array route; a separate bounded profile shows scoped native reads and disposal.

For maintainer source execution, build the pinned native asset, then run this project normally. An independent application consumes the same precompiled candidate through PackageReference and ordinary dotnet build/run/publish; `tests/Engines.SyncProof/verify.py` executes that path with Rust invocation traps. No input change requires a native rebuild. The sample owns every returned NativeBuffer with using.
