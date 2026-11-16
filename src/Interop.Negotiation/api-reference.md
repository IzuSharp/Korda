# NativeWeave.Interop negotiation API reference

Generated from the compiled product assembly and its XML comments by `tests/Interop.Negotiation/ApiCheck`. A02 metadata and module preflight only; native domain instances and executor ownership belong to downstream tasks.

## `F:NativeWeave.Interop.EngineThreadingModel.EngineLocalAsync`

```csharp
NativeWeave.Interop.EngineThreadingModel.EngineLocalAsync = 1
```

**summary:** Asynchronous work uses an instance-owned native executor.

## `F:NativeWeave.Interop.EngineThreadingModel.ExternalLoop`

```csharp
NativeWeave.Interop.EngineThreadingModel.ExternalLoop = 3
```

**summary:** An explicitly supplied external event loop controls progress; certification is separate.

## `F:NativeWeave.Interop.EngineThreadingModel.SerializedWorker`

```csharp
NativeWeave.Interop.EngineThreadingModel.SerializedWorker = 2
```

**summary:** Mutable native work uses a designated serialized worker.

## `F:NativeWeave.Interop.EngineThreadingModel.Synchronous`

```csharp
NativeWeave.Interop.EngineThreadingModel.Synchronous = 0
```

**summary:** Bounded work runs on the calling thread without a runtime.

## `M:NativeWeave.Interop.AbiVersion.#ctor`

```csharp
public NativeWeave.Interop.AbiVersion.AbiVersion()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.#ctor(System.UInt32,System.UInt32)`

```csharp
public NativeWeave.Interop.AbiVersion.AbiVersion(uint Major, uint Minor)
```

**summary:** Identifies the incompatible major and compatible minor versions of a native C ABI.

**param Major:** The representation and semantics version; manifests reject zero.

**param Minor:** The minimum compatible extension revision when used as a requirement.

**remarks:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.Deconstruct(System.UInt32@,System.UInt32@)`

```csharp
public void NativeWeave.Interop.AbiVersion.Deconstruct(out uint Major, out uint Minor)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.Equals(NativeWeave.Interop.AbiVersion)`

```csharp
public bool NativeWeave.Interop.AbiVersion.Equals(NativeWeave.Interop.AbiVersion other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.Equals(System.Object)`

```csharp
public override bool NativeWeave.Interop.AbiVersion.Equals(object obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.GetHashCode`

```csharp
public override int NativeWeave.Interop.AbiVersion.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.ToString`

```csharp
public override string NativeWeave.Interop.AbiVersion.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.op_Equality(NativeWeave.Interop.AbiVersion,NativeWeave.Interop.AbiVersion)`

```csharp
public static bool NativeWeave.Interop.AbiVersion.operator ==(NativeWeave.Interop.AbiVersion left, NativeWeave.Interop.AbiVersion right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.AbiVersion.op_Inequality(NativeWeave.Interop.AbiVersion,NativeWeave.Interop.AbiVersion)`

```csharp
public static bool NativeWeave.Interop.AbiVersion.operator !=(NativeWeave.Interop.AbiVersion left, NativeWeave.Interop.AbiVersion right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `M:NativeWeave.Interop.EngineCapability.#ctor`

```csharp
public NativeWeave.Interop.EngineCapability.EngineCapability()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCapability.#ctor(System.String)`

```csharp
public NativeWeave.Interop.EngineCapability.EngineCapability(string name)
```

**summary:** Creates a validated lower-case ASCII capability identifier.

**param name:** A dotted or hyphenated identifier of at most 128 ASCII bytes.

**exception:** The name is null.

**exception:** The name is empty, oversized or malformed.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCapability.Equals(NativeWeave.Interop.EngineCapability)`

```csharp
public bool NativeWeave.Interop.EngineCapability.Equals(NativeWeave.Interop.EngineCapability other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCapability.Equals(System.Object)`

```csharp
public override bool NativeWeave.Interop.EngineCapability.Equals(object obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCapability.GetHashCode`

```csharp
public override int NativeWeave.Interop.EngineCapability.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCapability.ToString`

```csharp
public override string NativeWeave.Interop.EngineCapability.ToString()
```

**summary:** Returns the existing identifier without loading native code or allocating another string.

**returns:** The identifier, or the empty string for the invalid default value.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCapability.op_Equality(NativeWeave.Interop.EngineCapability,NativeWeave.Interop.EngineCapability)`

```csharp
public static bool NativeWeave.Interop.EngineCapability.operator ==(NativeWeave.Interop.EngineCapability left, NativeWeave.Interop.EngineCapability right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCapability.op_Inequality(NativeWeave.Interop.EngineCapability,NativeWeave.Interop.EngineCapability)`

```csharp
public static bool NativeWeave.Interop.EngineCapability.operator !=(NativeWeave.Interop.EngineCapability left, NativeWeave.Interop.EngineCapability right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `M:NativeWeave.Interop.EngineCatalog.#ctor(System.Collections.Generic.IEnumerable{NativeWeave.Interop.EngineDescriptor})`

```csharp
public NativeWeave.Interop.EngineCatalog.EngineCatalog(System.Collections.Generic.IEnumerable<NativeWeave.Interop.EngineDescriptor> descriptors)
```

**summary:** Snapshots descriptors and rejects invalid or duplicate identities before publishing the catalog.

**param descriptors:** Explicitly selected, already compiled package descriptors.

**exception:** The collection is null.

**exception:** An entry is null, an identity is repeated, or more than 256 entries are observed.

**Inherited type policy:** Construction enumerates once and loads no native code. All subsequent reads are safe concurrently.

## `M:NativeWeave.Interop.EngineCatalog.Get(System.String)`

```csharp
public NativeWeave.Interop.EngineDescriptor NativeWeave.Interop.EngineCatalog.Get(string engineId)
```

**summary:** Gets a descriptor without loading a module.

**param engineId:** A valid case-sensitive manifest identity.

**returns:** The matching compiled descriptor.

**exception:** The identity is absent.

**exception:** The identity is malformed.

**exception:** The identity is null.

**Inherited type policy:** Construction enumerates once and loads no native code. All subsequent reads are safe concurrently.

## `M:NativeWeave.Interop.EngineCatalog.Inspect(System.String,NativeWeave.Interop.EngineLoadOptions)`

```csharp
public NativeWeave.Interop.EngineInfo NativeWeave.Interop.EngineCatalog.Inspect(string engineId, NativeWeave.Interop.EngineLoadOptions? options = null)
```

**summary:** Performs the same preflight as the selected descriptor.

**param engineId:** An identity contained in this catalog.

**param options:** Optional first-use load policy.

**returns:** Validated metadata without starting a domain runtime.

**exception:** The identity is absent.

**exception:** The identity or options are invalid.

**exception:** Preflight fails.

**Inherited type policy:** Construction enumerates once and loads no native code. All subsequent reads are safe concurrently.

## `M:NativeWeave.Interop.EngineCatalog.TryGet(System.String,NativeWeave.Interop.EngineDescriptor@)`

```csharp
public bool NativeWeave.Interop.EngineCatalog.TryGet(string engineId, [System.Diagnostics.CodeAnalysis.NotNullWhenAttribute(true)] out NativeWeave.Interop.EngineDescriptor? descriptor)
```

**summary:** Finds a descriptor without loading a module.

**param engineId:** A valid case-sensitive manifest identity.

**param descriptor:** The matching descriptor, or null when absent.

**returns:** True when this explicit catalog contains the identity.

**exception:** The identity is malformed.

**exception:** The identity is null.

**Inherited type policy:** Construction enumerates once and loads no native code. All subsequent reads are safe concurrently.

## `M:NativeWeave.Interop.EngineDescriptor.Inspect(NativeWeave.Interop.EngineLoadOptions)`

```csharp
public NativeWeave.Interop.EngineInfo NativeWeave.Interop.EngineDescriptor.Inspect(NativeWeave.Interop.EngineLoadOptions? options = null)
```

**summary:** Loads and validates this module without creating a domain instance or executor.

**param options:** Additional immutable-at-use load policy; null uses package defaults.

**returns:** Cached, validated metadata for the process-resident module.

**exception:** An option has an invalid path, capability or metadata bound.

**exception:** Platform, asset, identity, bootstrap or required capability validation fails.

**remarks:** First use reads and hashes the asset, loads it and calls bootstrap once. Compatible subsequent calls reuse metadata; explicit path overrides are verified. Options are snapshotted before file access. Calls are thread-safe and synchronous.

**Inherited type policy:** Metadata access performs no native load. Inspect validates local assets and caches one process-resident module; there is no disposal or hot-unload operation.

## `M:NativeWeave.Interop.EngineInfo.Require(NativeWeave.Interop.EngineCapability)`

```csharp
public void NativeWeave.Interop.EngineInfo.Require(NativeWeave.Interop.EngineCapability capability)
```

**summary:** Requires a capability before creating dependent domain resources.

**param capability:** The capability required by the caller.

**exception:** The capability is invalid.

**exception:** The actual binary does not advertise the capability.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `M:NativeWeave.Interop.EngineInfo.Supports(NativeWeave.Interop.EngineCapability)`

```csharp
public bool NativeWeave.Interop.EngineInfo.Supports(NativeWeave.Interop.EngineCapability capability)
```

**summary:** Checks a capability with no native call or per-call allocation.

**param capability:** A non-default capability name.

**returns:** True when the actual binary advertises the known capability; otherwise false.

**exception:** The capability is the invalid default value.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `M:NativeWeave.Interop.EngineLoadOptions.#ctor`

```csharp
public NativeWeave.Interop.EngineLoadOptions.EngineLoadOptions()
```

**summary:** Creates options using the package asset and no additional capability requirements.

**Inherited type policy:** Do not mutate a supplied collection during snapshot. There is no hash-bypass or native replacement option.

## `M:NativeWeave.Interop.EngineThreadingModel.#ctor`

```csharp
public NativeWeave.Interop.EngineThreadingModel.EngineThreadingModel()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.#ctor`

```csharp
public NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.EngineCapabilityDefinition()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.#ctor(NativeWeave.Interop.EngineCapability,System.Int32)`

```csharp
public NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.EngineCapabilityDefinition(NativeWeave.Interop.EngineCapability Capability, int BitIndex)
```

**summary:** A maintainer-defined immutable mapping from a capability name to an engine-local ABI bit.

**param Capability:** The validated stable capability name.

**param BitIndex:** A unique bit from 0 through 63; the manifest validates this value.

**remarks:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Deconstruct(NativeWeave.Interop.EngineCapability@,System.Int32@)`

```csharp
public void NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Deconstruct(out NativeWeave.Interop.EngineCapability Capability, out int BitIndex)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Equals(NativeWeave.Interop.Extensibility.EngineCapabilityDefinition)`

```csharp
public bool NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Equals(NativeWeave.Interop.Extensibility.EngineCapabilityDefinition other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Equals(System.Object)`

```csharp
public override bool NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Equals(object obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.GetHashCode`

```csharp
public override int NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.ToString`

```csharp
public override string NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.op_Equality(NativeWeave.Interop.Extensibility.EngineCapabilityDefinition,NativeWeave.Interop.Extensibility.EngineCapabilityDefinition)`

```csharp
public static bool NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.operator ==(NativeWeave.Interop.Extensibility.EngineCapabilityDefinition left, NativeWeave.Interop.Extensibility.EngineCapabilityDefinition right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.op_Inequality(NativeWeave.Interop.Extensibility.EngineCapabilityDefinition,NativeWeave.Interop.Extensibility.EngineCapabilityDefinition)`

```csharp
public static bool NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.operator !=(NativeWeave.Interop.Extensibility.EngineCapabilityDefinition left, NativeWeave.Interop.Extensibility.EngineCapabilityDefinition right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `M:NativeWeave.Interop.Extensibility.EnginePackage.CreateDescriptor(NativeWeave.Interop.Extensibility.EnginePackageManifest,System.Reflection.Assembly)`

```csharp
public static NativeWeave.Interop.EngineDescriptor NativeWeave.Interop.Extensibility.EnginePackage.CreateDescriptor(NativeWeave.Interop.Extensibility.EnginePackageManifest manifest, System.Reflection.Assembly assembly)
```

**summary:** Creates a descriptor without scanning assemblies, reading assets or loading native code.

**param manifest:** An already validated immutable loader manifest.

**param assembly:** The statically rooted facade assembly that owns generated native imports.

**returns:** An immutable descriptor for explicit catalog or ordinary domain use.

**exception:** Either argument is null.

**Inherited type policy:** Ordinary applications obtain descriptors through domain factories. No assembly discovery or managed plugin loading occurs.

## `M:NativeWeave.Interop.Extensibility.EnginePackageManifest.#ctor(System.String,System.String,System.String,NativeWeave.Interop.AbiVersion,System.String,NativeWeave.Interop.EngineThreadingModel,System.Collections.Generic.IReadOnlyList{NativeWeave.Interop.Extensibility.NativeAssetDescriptor},System.Collections.Generic.IReadOnlyList{NativeWeave.Interop.Extensibility.EngineCapabilityDefinition})`

```csharp
public NativeWeave.Interop.Extensibility.EnginePackageManifest.EnginePackageManifest(string engineId, string packageId, string packageVersion, NativeWeave.Interop.AbiVersion abiVersion, string bootstrapSymbol, NativeWeave.Interop.EngineThreadingModel threadingModel, System.Collections.Generic.IReadOnlyList<NativeWeave.Interop.Extensibility.NativeAssetDescriptor> assets, System.Collections.Generic.IReadOnlyList<NativeWeave.Interop.Extensibility.EngineCapabilityDefinition> capabilities)
```

**summary:** Validates and snapshots the bounded loader subset of a release manifest.

**param engineId:** A case-sensitive lower-case ASCII engine identity of at most 128 bytes.

**param packageId:** The managed facade NuGet identity, at most 256 UTF-8 bytes.

**param packageVersion:** The normalized NuGet version, at most 256 UTF-8 bytes.

**param abiVersion:** The required nonzero ABI major and minimum compatible minor.

**param bootstrapSymbol:** The exact engine-specific C bootstrap export, at most 128 ASCII bytes.

**param threadingModel:** The declared execution profile; construction starts no executor.

**param assets:** One through 64 immutable entries with unique RIDs and paths.

**param capabilities:** At most 64 unique non-default capability names and bit indices.

**exception:** A required argument or collection is null.

**exception:** Metadata is malformed, duplicated or exceeds a fixed bound.

**remarks:** Collections are enumerated once with bounds checked as entries arrive. Callers must not mutate them during construction; later mutations cannot change the manifest.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `M:NativeWeave.Interop.Extensibility.NativeAssetDescriptor.#ctor(System.String,System.String,System.String,System.String)`

```csharp
public NativeWeave.Interop.Extensibility.NativeAssetDescriptor.NativeAssetDescriptor(string runtimeIdentifier, string relativePath, string sha256, string cpuBaseline)
```

**summary:** Creates one exact portable-RID native asset entry.

**param runtimeIdentifier:** The portable linux-x64 or win-x64 RID.

**param relativePath:** A canonical path beneath runtimes/<rid>/native without traversal.

**param sha256:** The exact 64-character lowercase SHA-256.

**param cpuBaseline:** The nonempty published CPU profile, at most 256 UTF-8 bytes.

**exception:** An argument is null.

**exception:** An argument violates the bounded native-asset shape.

**Inherited type policy:** This is a package consistency declaration, not authentication or a sandbox for native code.

## `P:NativeWeave.Interop.AbiVersion.Major`

```csharp
public uint NativeWeave.Interop.AbiVersion.Major { get; init; }
```

**summary:** The representation and semantics version; manifests reject zero.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `P:NativeWeave.Interop.AbiVersion.Minor`

```csharp
public uint NativeWeave.Interop.AbiVersion.Minor { get; init; }
```

**summary:** The minimum compatible extension revision when used as a requirement.

**Inherited type policy:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `P:NativeWeave.Interop.EngineCapability.Name`

```csharp
public string NativeWeave.Interop.EngineCapability.Name { get; }
```

**summary:** Gets the existing identifier, or an empty string for the invalid default value.

**Inherited type policy:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `P:NativeWeave.Interop.EngineCatalog.Engines`

```csharp
public System.Collections.Generic.IReadOnlyList<NativeWeave.Interop.EngineDescriptor> NativeWeave.Interop.EngineCatalog.Engines { get; }
```

**summary:** Gets immutable descriptors in ordinal engine-identity order without loading modules.

**Inherited type policy:** Construction enumerates once and loads no native code. All subsequent reads are safe concurrently.

## `P:NativeWeave.Interop.EngineDescriptor.EngineId`

```csharp
public string NativeWeave.Interop.EngineDescriptor.EngineId { get; }
```

**summary:** Gets the stable case-sensitive engine identity without loading native code.

**Inherited type policy:** Metadata access performs no native load. Inspect validates local assets and caches one process-resident module; there is no disposal or hot-unload operation.

## `P:NativeWeave.Interop.EngineDescriptor.PackageId`

```csharp
public string NativeWeave.Interop.EngineDescriptor.PackageId { get; }
```

**summary:** Gets the facade package identity.

**Inherited type policy:** Metadata access performs no native load. Inspect validates local assets and caches one process-resident module; there is no disposal or hot-unload operation.

## `P:NativeWeave.Interop.EngineDescriptor.PackageVersion`

```csharp
public string NativeWeave.Interop.EngineDescriptor.PackageVersion { get; }
```

**summary:** Gets the normalized package version.

**Inherited type policy:** Metadata access performs no native load. Inspect validates local assets and caches one process-resident module; there is no disposal or hot-unload operation.

## `P:NativeWeave.Interop.EngineDescriptor.SupportedRuntimeIdentifiers`

```csharp
public System.Collections.Generic.IReadOnlyList<string> NativeWeave.Interop.EngineDescriptor.SupportedRuntimeIdentifiers { get; }
```

**summary:** Gets the exact immutable packaged RID list, without applying a fallback graph.

**Inherited type policy:** Metadata access performs no native load. Inspect validates local assets and caches one process-resident module; there is no disposal or hot-unload operation.

## `P:NativeWeave.Interop.EngineInfo.AbiVersion`

```csharp
public NativeWeave.Interop.AbiVersion NativeWeave.Interop.EngineInfo.AbiVersion { get; }
```

**summary:** Gets the version reported by the validated native bootstrap.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `P:NativeWeave.Interop.EngineInfo.Capabilities`

```csharp
public System.Collections.Generic.IReadOnlyList<NativeWeave.Interop.EngineCapability> NativeWeave.Interop.EngineInfo.Capabilities { get; }
```

**summary:** Gets an immutable ordinal snapshot of known names advertised by the actual binary.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `P:NativeWeave.Interop.EngineInfo.EngineId`

```csharp
public string NativeWeave.Interop.EngineInfo.EngineId { get; }
```

**summary:** Gets the manifest engine identity, independent of any domain instance.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `P:NativeWeave.Interop.EngineInfo.PackageId`

```csharp
public string NativeWeave.Interop.EngineInfo.PackageId { get; }
```

**summary:** Gets the managed facade package identity.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `P:NativeWeave.Interop.EngineInfo.PackageVersion`

```csharp
public string NativeWeave.Interop.EngineInfo.PackageVersion { get; }
```

**summary:** Gets the normalized NuGet version without a lossy System.Version conversion.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `P:NativeWeave.Interop.EngineInfo.RuntimeIdentifier`

```csharp
public string NativeWeave.Interop.EngineInfo.RuntimeIdentifier { get; }
```

**summary:** Gets the selected exact portable runtime identifier.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `P:NativeWeave.Interop.EngineInfo.ThreadingModel`

```csharp
public NativeWeave.Interop.EngineThreadingModel NativeWeave.Interop.EngineInfo.ThreadingModel { get; }
```

**summary:** Gets the declared execution profile; reading it starts no executor.

**Inherited type policy:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `P:NativeWeave.Interop.EngineLoadOptions.NativeAssetPath`

```csharp
public string? NativeWeave.Interop.EngineLoadOptions.NativeAssetPath { get; init; }
```

**summary:** Gets or initializes an absolute asset path, still subject to exact RID, format and hash validation.

**Inherited type policy:** Do not mutate a supplied collection during snapshot. There is no hash-bypass or native replacement option.

## `P:NativeWeave.Interop.EngineLoadOptions.RequiredCapabilities`

```csharp
public System.Collections.Generic.IReadOnlyList<NativeWeave.Interop.EngineCapability> NativeWeave.Interop.EngineLoadOptions.RequiredCapabilities { get; init; }
```

**summary:** Gets or initializes at most 64 unique non-default capabilities required before use.

**Inherited type policy:** Do not mutate a supplied collection during snapshot. There is no hash-bypass or native replacement option.

## `P:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.BitIndex`

```csharp
public int NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.BitIndex { get; init; }
```

**summary:** A unique bit from 0 through 63; the manifest validates this value.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `P:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Capability`

```csharp
public NativeWeave.Interop.EngineCapability NativeWeave.Interop.Extensibility.EngineCapabilityDefinition.Capability { get; init; }
```

**summary:** The validated stable capability name.

**Inherited type policy:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.AbiVersion`

```csharp
public NativeWeave.Interop.AbiVersion NativeWeave.Interop.Extensibility.EnginePackageManifest.AbiVersion { get; }
```

**summary:** Gets the required major and minimum compatible minor ABI versions.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.Assets`

```csharp
public System.Collections.Generic.IReadOnlyList<NativeWeave.Interop.Extensibility.NativeAssetDescriptor> NativeWeave.Interop.Extensibility.EnginePackageManifest.Assets { get; }
```

**summary:** Gets the immutable native asset snapshot.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.BootstrapSymbol`

```csharp
public string NativeWeave.Interop.Extensibility.EnginePackageManifest.BootstrapSymbol { get; }
```

**summary:** Gets the exact bootstrap export identifier.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.Capabilities`

```csharp
public System.Collections.Generic.IReadOnlyList<NativeWeave.Interop.Extensibility.EngineCapabilityDefinition> NativeWeave.Interop.Extensibility.EnginePackageManifest.Capabilities { get; }
```

**summary:** Gets the immutable engine-local capability map.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.EngineId`

```csharp
public string NativeWeave.Interop.Extensibility.EnginePackageManifest.EngineId { get; }
```

**summary:** Gets the stable case-sensitive engine identity.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.PackageId`

```csharp
public string NativeWeave.Interop.Extensibility.EnginePackageManifest.PackageId { get; }
```

**summary:** Gets the facade package identity.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.PackageVersion`

```csharp
public string NativeWeave.Interop.Extensibility.EnginePackageManifest.PackageVersion { get; }
```

**summary:** Gets the normalized package version.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.EnginePackageManifest.ThreadingModel`

```csharp
public NativeWeave.Interop.EngineThreadingModel NativeWeave.Interop.Extensibility.EnginePackageManifest.ThreadingModel { get; }
```

**summary:** Gets the declared execution profile without starting it.

**Inherited type policy:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `P:NativeWeave.Interop.Extensibility.NativeAssetDescriptor.CpuBaseline`

```csharp
public string NativeWeave.Interop.Extensibility.NativeAssetDescriptor.CpuBaseline { get; }
```

**summary:** Gets the published CPU baseline, checked during preflight.

**Inherited type policy:** This is a package consistency declaration, not authentication or a sandbox for native code.

## `P:NativeWeave.Interop.Extensibility.NativeAssetDescriptor.RelativePath`

```csharp
public string NativeWeave.Interop.Extensibility.NativeAssetDescriptor.RelativePath { get; }
```

**summary:** Gets the canonical package-relative native source path.

**Inherited type policy:** This is a package consistency declaration, not authentication or a sandbox for native code.

## `P:NativeWeave.Interop.Extensibility.NativeAssetDescriptor.RuntimeIdentifier`

```csharp
public string NativeWeave.Interop.Extensibility.NativeAssetDescriptor.RuntimeIdentifier { get; }
```

**summary:** Gets the portable RID; its presence does not assert that platform's execution passed.

**Inherited type policy:** This is a package consistency declaration, not authentication or a sandbox for native code.

## `P:NativeWeave.Interop.Extensibility.NativeAssetDescriptor.Sha256`

```csharp
public string NativeWeave.Interop.Extensibility.NativeAssetDescriptor.Sha256 { get; }
```

**summary:** Gets the expected lowercase asset digest.

**Inherited type policy:** This is a package consistency declaration, not authentication or a sandbox for native code.

## `T:NativeWeave.Interop.AbiVersion`

```csharp
public readonly record struct NativeWeave.Interop.AbiVersion : System.IEquatable<NativeWeave.Interop.AbiVersion>
```

**summary:** Identifies the incompatible major and compatible minor versions of a native C ABI.

**param Major:** The representation and semantics version; manifests reject zero.

**param Minor:** The minimum compatible extension revision when used as a requirement.

**remarks:** An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.

## `T:NativeWeave.Interop.EngineCapability`

```csharp
public readonly record struct NativeWeave.Interop.EngineCapability : System.IEquatable<NativeWeave.Interop.EngineCapability>
```

**summary:** A case-sensitive, versioned capability name, independent of loading or engine ownership.

**remarks:** Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.

## `T:NativeWeave.Interop.EngineCatalog`

```csharp
public sealed class NativeWeave.Interop.EngineCatalog
```

**summary:** An immutable, explicit index of at most 256 descriptors; it never scans assemblies or directories.

**remarks:** Construction enumerates once and loads no native code. All subsequent reads are safe concurrently.

## `T:NativeWeave.Interop.EngineDescriptor`

```csharp
public sealed class NativeWeave.Interop.EngineDescriptor
```

**summary:** A statically compiled immutable package descriptor, normally supplied by a domain factory.

**remarks:** Metadata access performs no native load. Inspect validates local assets and caches one process-resident module; there is no disposal or hot-unload operation.

## `T:NativeWeave.Interop.EngineInfo`

```csharp
public sealed class NativeWeave.Interop.EngineInfo
```

**summary:** An immutable snapshot from a module that passed actual native bootstrap validation.

**remarks:** All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.

## `T:NativeWeave.Interop.EngineLoadOptions`

```csharp
public sealed class NativeWeave.Interop.EngineLoadOptions
```

**summary:** Advanced first-use loading options; domain builders and Inspect snapshot the values before use.

**remarks:** Do not mutate a supplied collection during snapshot. There is no hash-bypass or native replacement option.

## `T:NativeWeave.Interop.EngineThreadingModel`

```csharp
public enum NativeWeave.Interop.EngineThreadingModel : int
```

**summary:** The declared native execution profile; it does not grant public thread-safety guarantees.

## `T:NativeWeave.Interop.Extensibility.EngineCapabilityDefinition`

```csharp
public readonly record struct NativeWeave.Interop.Extensibility.EngineCapabilityDefinition : System.IEquatable<NativeWeave.Interop.Extensibility.EngineCapabilityDefinition>
```

**summary:** A maintainer-defined immutable mapping from a capability name to an engine-local ABI bit.

**param Capability:** The validated stable capability name.

**param BitIndex:** A unique bit from 0 through 63; the manifest validates this value.

**remarks:** Value equality has no ownership effects. Different engines may assign different names to the same bit.

## `T:NativeWeave.Interop.Extensibility.EnginePackage`

```csharp
public static class NativeWeave.Interop.Extensibility.EnginePackage
```

**summary:** The explicit AOT-safe maintainer entry point for statically compiled engine packages.

**remarks:** Ordinary applications obtain descriptors through domain factories. No assembly discovery or managed plugin loading occurs.

## `T:NativeWeave.Interop.Extensibility.EnginePackageManifest`

```csharp
public sealed class NativeWeave.Interop.Extensibility.EnginePackageManifest
```

**summary:** Immutable loader inputs from a complete maintainer release manifest.

**remarks:** Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.

## `T:NativeWeave.Interop.Extensibility.NativeAssetDescriptor`

```csharp
public sealed class NativeWeave.Interop.Extensibility.NativeAssetDescriptor
```

**summary:** An immutable, maintainer-generated native asset descriptor; construction reads no file.

**remarks:** This is a package consistency declaration, not authentication or a sandbox for native code.
