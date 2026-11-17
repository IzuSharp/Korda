# Internal Task bridge reference

Generated from the compiled NativeWeave.Core assembly and XML by `tests/Interop.Async/ApiCheck`.

A07 adds **zero public types or members**. Exact domain Async methods return `Task<TResult>` and use their own typed native create/start/take ABI. The callback cookie, control leases, terminal latch and retirement coordinator remain internal.

**Implementation summary:** Nongeneric static reverse-P/Invoke, setup/cancellation drain and native retirement owner.

The compiled static nongeneric UCO entry uses exact Cdecl `(nuint, uint, NwErrorInfoV1*) -> uint`. Six namespace types are internal. `AsyncOperation<TResult>.Start()` returns `Task<TResult>`; TResult never crosses the ABI. An unrelated consumer is rejected with CS0122. XML documents setup settlement, external cancellation drain, one-time typed result transfer and independent native retirement.
