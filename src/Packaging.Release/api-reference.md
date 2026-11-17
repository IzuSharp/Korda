# Packaging compiler-host task reference

Generated from the actual compiled assembly and XML by `tests/Packaging.Release/ApiCheck`. This MSBuild host seam is shipped under `tools/net11.0`; it is absent from application runtime/AOT output. Application APIs remain owned by Core.

## `M:NativeWeave.Packaging.PackageClosure.#ctor`

```csharp
public PackageClosure()
```

**summary:** Creates an unconfigured, single-invocation compiler-host task.

## `M:NativeWeave.Packaging.PackageClosure.Execute`

```csharp
public override bool Execute()
```

**summary:** Checks the restored graph and, when requested, emits each native record atomically.

**returns:** True only if all identities, exact edges, native bytes and SDK destinations match.

**remarks:** Expected malformed metadata, IO and compatibility failures become hard NWPKG errors even when NuGet warnings are suppressed. No partial result is returned as a successful closure.

## `P:NativeWeave.Packaging.PackageClosure.AssetsFile`

```csharp
[Required] public string AssetsFile { get; set; }
```

**summary:** Gets or sets the SDK's resolved project.assets.json path.

**value:** The existing restore output, never a guessed global package cache.

## `P:NativeWeave.Packaging.PackageClosure.ClosureFiles`

```csharp
[Required] public Microsoft.Build.Framework.ITaskItem[] ClosureFiles { get; set; }
```

**summary:** Gets or sets the exact closure documents supplied transitively by the selected packages.

**value:** One through 256 bounded JSON closure files.

## `P:NativeWeave.Packaging.PackageClosure.Mode`

```csharp
public string Mode { get; set; }
```

**summary:** Gets or sets validate, build, or publish mode.

**value:** The validation-only default or an SDK record generation phase.

## `P:NativeWeave.Packaging.PackageClosure.OutputDirectory`

```csharp
public string OutputDirectory { get; set; }
```

**summary:** Gets or sets the record output directory for build or publish mode.

**value:** The SDK-owned output or intermediate publication directory.

## `P:NativeWeave.Packaging.PackageClosure.Records`

```csharp
[Output] public Microsoft.Build.Framework.ITaskItem[] Records { get; private set; }
```

**summary:** Gets generated records and their output-relative paths for the SDK publish pipeline.

**value:** Only successfully written records from this invocation; empty initially.

## `P:NativeWeave.Packaging.PackageClosure.ResolvedFiles`

```csharp
public Microsoft.Build.Framework.ITaskItem[] ResolvedFiles { get; set; }
```

**summary:** Gets or sets SDK-resolved native or publish items, preserving their destination metadata.

**value:** Actual SDK item identities and package/destination metadata.

## `P:NativeWeave.Packaging.PackageClosure.RuntimeIdentifier`

```csharp
public string RuntimeIdentifier { get; set; }
```

**summary:** Gets or sets the explicit RID; an empty value uses the current SDK host RID.

**value:** A supported exact RID or the empty string.

## `T:NativeWeave.Packaging.PackageClosure`

```csharp
public sealed class NativeWeave.Packaging.PackageClosure : Microsoft.Build.Utilities.Task
```

**summary:** Validates a restored owned-package closure and produces SDK-selected native output records.

**remarks:** This is an MSBuild host task, never an application runtime dependency. Each invocation is independent. It reads bounded package metadata, hashes native files and performs no network, restore, Rust compilation or native loading.
