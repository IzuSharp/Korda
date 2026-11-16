# NativeWeave.Interop errors API reference

Generated from the compiled product assembly and its XML comments by `tests/Interop.Errors/ApiCheck`. A05 failure metadata only; transaction and provider outcomes retain their domain owners.

## `F:NativeWeave.Interop.EngineLoadFailure.AssetIntegrityMismatch`

```csharp
NativeWeave.Interop.EngineLoadFailure.AssetIntegrityMismatch = 4
```

**summary:** Content differs from the package manifest.

## `F:NativeWeave.Interop.EngineLoadFailure.ConflictingModule`

```csharp
NativeWeave.Interop.EngineLoadFailure.ConflictingModule = 8
```

**summary:** Another content identity was selected for this engine.

## `F:NativeWeave.Interop.EngineLoadFailure.IncompatibleAbi`

```csharp
NativeWeave.Interop.EngineLoadFailure.IncompatibleAbi = 6
```

**summary:** The ABI major or required record shape is incompatible.

## `F:NativeWeave.Interop.EngineLoadFailure.InvalidBinary`

```csharp
NativeWeave.Interop.EngineLoadFailure.InvalidBinary = 3
```

**summary:** The format, architecture or CPU baseline is incompatible.

## `F:NativeWeave.Interop.EngineLoadFailure.InvalidManifest`

```csharp
NativeWeave.Interop.EngineLoadFailure.InvalidManifest = 9
```

**summary:** The manifest is malformed or contradicts the selected asset.

## `F:NativeWeave.Interop.EngineLoadFailure.MissingAsset`

```csharp
NativeWeave.Interop.EngineLoadFailure.MissingAsset = 2
```

**summary:** The native asset or an OS dependency is missing.

## `F:NativeWeave.Interop.EngineLoadFailure.MissingCapability`

```csharp
NativeWeave.Interop.EngineLoadFailure.MissingCapability = 7
```

**summary:** A required capability is absent.

## `F:NativeWeave.Interop.EngineLoadFailure.MissingExport`

```csharp
NativeWeave.Interop.EngineLoadFailure.MissingExport = 5
```

**summary:** A required bootstrap export is absent.

## `F:NativeWeave.Interop.EngineLoadFailure.UnsupportedPlatform`

```csharp
NativeWeave.Interop.EngineLoadFailure.UnsupportedPlatform = 1
```

**summary:** The current runtime identifier has no supported asset.

## `M:NativeWeave.Interop.EngineLoadException.#ctor(NativeWeave.Interop.EngineLoadFailure,System.String,System.String,System.String,System.Exception)`

```csharp
public NativeWeave.Interop.EngineLoadException.EngineLoadException(NativeWeave.Interop.EngineLoadFailure failure, string engineId, string runtimeIdentifier, string message, System.Exception? innerException = null)
```

**summary:** Creates a managed preflight diagnostic without fabricating a native record.

**param failure:** A defined load-failure classification.

**param engineId:** The requested engine identity.

**param runtimeIdentifier:** The requested nonempty runtime identifier.

**param message:** A redacted message with an installation or compatibility remedy.

**param innerException:** The optional loader cause.

**exception:** An identity, runtime identifier or message is invalid.

**exception:** The failure classification is unknown.

## `M:NativeWeave.Interop.EngineLoadFailure.#ctor`

```csharp
public NativeWeave.Interop.EngineLoadFailure.EngineLoadFailure()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeEngineException.#ctor`

```csharp
public NativeWeave.Interop.NativeEngineException.NativeEngineException()
```

**summary:** Creates an exception without native diagnostic metadata.

## `M:NativeWeave.Interop.NativeEngineException.#ctor(System.String)`

```csharp
public NativeWeave.Interop.NativeEngineException.NativeEngineException(string? message)
```

**summary:** Creates an exception without native diagnostic metadata.

**param message:** The diagnostic message, or null.

## `M:NativeWeave.Interop.NativeEngineException.#ctor(System.String,System.Exception)`

```csharp
public NativeWeave.Interop.NativeEngineException.NativeEngineException(string? message, System.Exception? innerException)
```

**summary:** Preserves a managed cause without inventing a native record.

**param message:** The diagnostic message, or null.

**param innerException:** The managed cause, or null.

## `M:NativeWeave.Interop.NativeEngineException.#ctor(System.String,System.String,System.UInt32,System.UInt32,System.Int32,System.UInt64,System.String,System.Exception)`

```csharp
public NativeWeave.Interop.NativeEngineException.NativeEngineException(string engineId, string operation, uint statusCode, uint errorDomain, int nativeCode, ulong correlationId, string? message, System.Exception? innerException = null)
```

**summary:** Creates an exception preserving a complete native failure record.

**param engineId:** The ordinal engine identifier, using the documented ASCII identity grammar.

**param operation:** The nonempty stable adapter operation name.

**param statusCode:** The original wire failure, including unknown values; zero and one are invalid.

**param errorDomain:** The original unsigned error domain.

**param nativeCode:** The original signed provider code.

**param correlationId:** The original opaque correlation value; zero is permitted.

**param message:** A bounded, redacted diagnostic message, or null.

**param innerException:** The managed bridge cause, or null.

**exception:** An identity or operation is invalid.

**exception:** The status denotes success or pending.

## `P:NativeWeave.Interop.EngineLoadException.Failure`

```csharp
public NativeWeave.Interop.EngineLoadFailure NativeWeave.Interop.EngineLoadException.Failure { get; }
```

**summary:** Gets the immutable preflight classification.

## `P:NativeWeave.Interop.EngineLoadException.RuntimeIdentifier`

```csharp
public string NativeWeave.Interop.EngineLoadException.RuntimeIdentifier { get; }
```

**summary:** Gets the requested runtime identifier.

## `P:NativeWeave.Interop.NativeEngineException.CorrelationId`

```csharp
public ulong? NativeWeave.Interop.NativeEngineException.CorrelationId { get; }
```

**summary:** Gets the correlation value, or null without a native record.

## `P:NativeWeave.Interop.NativeEngineException.EngineId`

```csharp
public string? NativeWeave.Interop.NativeEngineException.EngineId { get; }
```

**summary:** Gets the immutable engine identity, or null without engine context.

## `P:NativeWeave.Interop.NativeEngineException.ErrorDomain`

```csharp
public uint? NativeWeave.Interop.NativeEngineException.ErrorDomain { get; }
```

**summary:** Gets the original error domain, or null without a native record.

## `P:NativeWeave.Interop.NativeEngineException.NativeCode`

```csharp
public int? NativeWeave.Interop.NativeEngineException.NativeCode { get; }
```

**summary:** Gets the signed provider code, or null without a native record.

## `P:NativeWeave.Interop.NativeEngineException.Operation`

```csharp
public string? NativeWeave.Interop.NativeEngineException.Operation { get; }
```

**summary:** Gets the immutable operation name, or null without engine context.

## `P:NativeWeave.Interop.NativeEngineException.StatusCode`

```csharp
public uint? NativeWeave.Interop.NativeEngineException.StatusCode { get; }
```

**summary:** Gets the unmodified wire status, or null without a native record.

## `T:NativeWeave.Interop.EngineLoadException`

```csharp
public sealed class NativeWeave.Interop.EngineLoadException : NativeWeave.Interop.NativeEngineException
```

**summary:** An actionable loading failure before domain resources are published.

**remarks:** EngineId and Operation identify preflight; native-record fields stay null. No fallback or retry is performed.

## `T:NativeWeave.Interop.EngineLoadFailure`

```csharp
public enum NativeWeave.Interop.EngineLoadFailure : int
```

**summary:** Classifies a managed module preflight failure; these values are not C ABI status values.

## `T:NativeWeave.Interop.NativeEngineException`

```csharp
public class NativeWeave.Interop.NativeEngineException : System.Exception
```

**summary:** A native panic, poisoned engine or interop contract failure with optional native diagnostics.

**remarks:** Construction is a cold error path. Instances own immutable managed diagnostic values and retain no native owner. Ordinary domain errors use their domain exception. A failure never proves rollback, retirement or safe retry.

