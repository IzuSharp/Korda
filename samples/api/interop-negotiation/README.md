# Explicit engine preflight

The A02 verifier builds and executes this sample in ordinary .NET and Linux x64
Native AOT. It installs two prebuilt, stateless native fixture modules and emits
their exact SDK output-location records; the sample itself performs no Rust
build and uses the production Core metadata/catalog/loader implementation.

The sample shows minimal catalog preflight and advanced required-capability
checks through the same loader, including actionable missing-capability handling
and two independent static imports. The fixture hashes are supplied by the
maintainer verifier; real engine facades embed these values in their generated
package descriptors. Application Entity/DTO/Handler changes do not participate
in native compilation.

See [the guide](../../../docs/guides/interop-negotiation.md) for exact lifecycle,
concurrency, costs and failure semantics. Evidence state is `product-executed`
only for the commands and hashes captured by the corresponding A02 run; these
fixtures make no database, server, all-platform or released-package claim.
