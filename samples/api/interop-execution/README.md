# Optional execution examples

This is the A08 maintainer-only concrete engine adapter, linked against the actual
internal A07/A08 implementation and opt-in native conformance adapter. It adds no
public application scheduler or package. The first example awaits a typed native
result and actual async disposal. The advanced example times out a close wait
while blocking work keeps its root and byte budget, then observes actual worker
join after that work really returns.

Run `python3 tests/Interop.Execution/verify.py` from the implementation root with
the pinned toolchain. It compiles and executes both paths under ordinary .NET and
explicit Linux x64 Native AOT. See the [ownership and cost guide](../../../docs/guides/interop-execution.md)
and generated `src/Interop.Execution/api-reference.md` for the internal contract.
