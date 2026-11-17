# Internal resource ownership reference

Generated from the compiled NativeWeave.Core assembly and XML by `tests/Interop.Lifetime/ApiCheck`.

A03 adds **zero public types or members**. Its sealed internal NativeResourceOwner supplies logical admission, SafeHandle leasing, nonblocking closure and a separate retirement observation. No public copyable owning struct, raw-pointer factory or new NuGet package is introduced.

**Implementation summary:** Internal D04 logical admission and physical SafeHandle leases.

Public immutable buffer members are owned and checked by A04. This internal component has no application API compatibility promise; generated adapters compile directly against its shared implementation.
