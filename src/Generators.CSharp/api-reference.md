# NativeWeave C# generation API reference

Generated from compiled assembly metadata and XML documentation.

The maintainer executable and generated runtime bindings expose **0 public application types and 0 public application members**.

The separate netstandard2.0 compiler-host entry point exports one sealed type implementing `Microsoft.CodeAnalysis.IIncrementalGenerator`:

- `T:NativeWeave.Generators.CSharp.Metadata.BindingMetadataGenerator` — Emits static metadata from the maintainer's validated description without generating imports.
- `M:NativeWeave.Generators.CSharp.Metadata.BindingMetadataGenerator.#ctor` — Creates the stateless compiler-host generator.
- `M:NativeWeave.Generators.CSharp.Metadata.BindingMetadataGenerator.Initialize(Microsoft.CodeAnalysis.IncrementalGeneratorInitializationContext)` — Registers value-comparable AdditionalText transforms and deterministic static source output.

`Initialize` takes the required value-type `context` parameter and returns void. The stateless constructor has no parameters. There are no optional defaults or additional public members.

The compiler invokes this build-only surface; applications do not load it at runtime. See [C# bindings and compiler metadata](../../docs/guides/generators-csharp.md).
