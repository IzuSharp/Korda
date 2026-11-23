# Korda.Interop errors API reference

Generated from the compiled product assembly and its XML comments by `tests/Interop.Errors/ApiCheck`. A05 failure metadata only; transaction and provider outcomes retain their domain owners.

## `F:Korda.Interop.EngineLoadFailure.AssetIntegrityMismatch`

```csharp
Korda.Interop.EngineLoadFailure.AssetIntegrityMismatch = 4
```

**summary:** Content differs from the package manifest.

## `F:Korda.Interop.EngineLoadFailure.ConflictingModule`

```csharp
Korda.Interop.EngineLoadFailure.ConflictingModule = 8
```

**summary:** Another content identity was selected for this engine.

## `F:Korda.Interop.EngineLoadFailure.IncompatibleAbi`

```csharp
Korda.Interop.EngineLoadFailure.IncompatibleAbi = 6
```

**summary:** The ABI major or required record shape is incompatible.

## `F:Korda.Interop.EngineLoadFailure.InvalidBinary`

```csharp
Korda.Interop.EngineLoadFailure.InvalidBinary = 3
```

**summary:** The format, architecture or CPU baseline is incompatible.

## `F:Korda.Interop.EngineLoadFailure.InvalidManifest`

```csharp
Korda.Interop.EngineLoadFailure.InvalidManifest = 9
```

**summary:** The manifest is malformed or contradicts the selected asset.

## `F:Korda.Interop.EngineLoadFailure.MissingAsset`

```csharp
Korda.Interop.EngineLoadFailure.MissingAsset = 2
```

**summary:** The native asset or an OS dependency is missing.

## `F:Korda.Interop.EngineLoadFailure.MissingCapability`

```csharp
Korda.Interop.EngineLoadFailure.MissingCapability = 7
```

**summary:** A required capability is absent.

## `F:Korda.Interop.EngineLoadFailure.MissingExport`

```csharp
Korda.Interop.EngineLoadFailure.MissingExport = 5
```

**summary:** A required bootstrap export is absent.

## `F:Korda.Interop.EngineLoadFailure.UnsupportedPlatform`

```csharp
Korda.Interop.EngineLoadFailure.UnsupportedPlatform = 1
```

**summary:** The current runtime identifier has no supported asset.

## `M:Korda.Interop.EngineLoadException.#ctor(Korda.Interop.EngineLoadFailure,System.String,System.String,System.String,System.Exception)`

```csharp
public Korda.Interop.EngineLoadException.EngineLoadException(Korda.Interop.EngineLoadFailure failure, string engineId, string runtimeIdentifier, string message, System.Exception? innerException = null)
```

**summary:** Creates a managed preflight diagnostic without fabricating a native record.

**param failure:** A defined load-failure classification.

**param engineId:** The requested engine identity.

**param runtimeIdentifier:** The requested nonempty runtime identifier.

**param message:** A redacted message with an installation or compatibility remedy.

**param innerException:** The optional loader cause.

**exception:** An identity, runtime identifier or message is invalid.

**exception:** The failure classification is unknown.

## `M:Korda.Interop.EngineLoadFailure.#ctor`

```csharp
public Korda.Interop.EngineLoadFailure.EngineLoadFailure()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Interop.NativeEngineException.#ctor`

```csharp
public Korda.Interop.NativeEngineException.NativeEngineException()
```

**summary:** Creates an exception without native diagnostic metadata.

## `M:Korda.Interop.NativeEngineException.#ctor(System.String)`

```csharp
public Korda.Interop.NativeEngineException.NativeEngineException(string? message)
```

**summary:** Creates an exception without native diagnostic metadata.

**param message:** The diagnostic message, or null.

## `M:Korda.Interop.NativeEngineException.#ctor(System.String,System.Exception)`

```csharp
public Korda.Interop.NativeEngineException.NativeEngineException(string? message, System.Exception? innerException)
```

**summary:** Preserves a managed cause without inventing a native record.

**param message:** The diagnostic message, or null.

**param innerException:** The managed cause, or null.

## `M:Korda.Interop.NativeEngineException.#ctor(System.String,System.String,System.UInt32,System.UInt32,System.Int32,System.UInt64,System.String,System.Exception)`

```csharp
public Korda.Interop.NativeEngineException.NativeEngineException(string engineId, string operation, uint statusCode, uint errorDomain, int nativeCode, ulong correlationId, string? message, System.Exception? innerException = null)
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

## `P:Korda.Interop.EngineLoadException.Failure`

```csharp
public Korda.Interop.EngineLoadFailure Korda.Interop.EngineLoadException.Failure { get; }
```

**summary:** Gets the immutable preflight classification.

## `P:Korda.Interop.EngineLoadException.RuntimeIdentifier`

```csharp
public string Korda.Interop.EngineLoadException.RuntimeIdentifier { get; }
```

**summary:** Gets the requested runtime identifier.

## `P:Korda.Interop.NativeEngineException.CorrelationId`

```csharp
public ulong? Korda.Interop.NativeEngineException.CorrelationId { get; }
```

**summary:** Gets the correlation value, or null without a native record.

## `P:Korda.Interop.NativeEngineException.EngineId`

```csharp
public string? Korda.Interop.NativeEngineException.EngineId { get; }
```

**summary:** Gets the immutable engine identity, or null without engine context.

## `P:Korda.Interop.NativeEngineException.ErrorDomain`

```csharp
public uint? Korda.Interop.NativeEngineException.ErrorDomain { get; }
```

**summary:** Gets the original error domain, or null without a native record.

## `P:Korda.Interop.NativeEngineException.NativeCode`

```csharp
public int? Korda.Interop.NativeEngineException.NativeCode { get; }
```

**summary:** Gets the signed provider code, or null without a native record.

## `P:Korda.Interop.NativeEngineException.Operation`

```csharp
public string? Korda.Interop.NativeEngineException.Operation { get; }
```

**summary:** Gets the immutable operation name, or null without engine context.

## `P:Korda.Interop.NativeEngineException.StatusCode`

```csharp
public uint? Korda.Interop.NativeEngineException.StatusCode { get; }
```

**summary:** Gets the unmodified wire status, or null without a native record.

## `T:Korda.Interop.EngineLoadException`

```csharp
public sealed class Korda.Interop.EngineLoadException : Korda.Interop.NativeEngineException
```

**summary:** An actionable loading failure before domain resources are published.

**remarks:** EngineId and Operation identify preflight; native-record fields stay null. No fallback or retry is performed.

## `T:Korda.Interop.EngineLoadFailure`

```csharp
public enum Korda.Interop.EngineLoadFailure : int
```

**summary:** Classifies a managed module preflight failure; these values are not C ABI status values.

## `T:Korda.Interop.NativeEngineException`

```csharp
public class Korda.Interop.NativeEngineException : System.Exception
```

**summary:** A native panic, poisoned engine or interop contract failure with optional native diagnostics.

**remarks:** Construction is a cold error path. Instances own immutable managed diagnostic values and retain no native owner. Ordinary domain errors use their domain exception. A failure never proves rollback, retirement or safe retry.

