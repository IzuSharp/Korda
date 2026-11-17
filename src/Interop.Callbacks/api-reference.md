# Internal callback registration reference

Generated from the compiled NativeWeave.Core assembly and XML by `tests/Interop.Callbacks/ApiCheck`.

A06 adds **zero public types or members**. Application handlers use their domain's typed managed API; the callback table, strong root, native controls and dispatcher remain internal.

**Implementation summary:** Internal D05 subscription owner, strong cookie root and separately drained dispatcher.

The compiled static nongeneric UCO entry uses exact Cdecl `(nuint, uint, void*, ulong) -> uint`. Its eight namespace types are internal; an unrelated consumer is rejected with CS0122. The registration XML records two-sided retirement, ownership and bounded dispatcher semantics. Existing public Core signatures remain unchanged.
