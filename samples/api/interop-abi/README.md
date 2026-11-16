# Internal ABI maintainer sample

This is an explicit friend-assembly sample for A01's internal wire seam. It is
not a public application API, a database provider, a server or a released NuGet
package. `Program.cs` performs a typed import into the test-only conformance
engine, which calls the actual product ABI writer and initializes no runtime.

Run `python3 tests/Interop.Abi/verify.py` from the repository root. The verifier
builds the exact native fixture, copies it as a normal adjacent native asset and
runs the minimal example, `--advanced` capability rejection and actual Linux x64
Native AOT executions. It supplies `A01NativeFixture` only to these maintainer
projects; consumers are never asked to build Rust through MSBuild.

Sample states and provenance are recorded by actual verification attempts and
the Plan `ledger/evidence/A01/` handoff. The complete record/ownership/error/cost
contract is in [the ABI guide](../../../docs/guides/interop-abi.md).
