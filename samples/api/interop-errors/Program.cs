using Korda.Interop;

// Minimal: inspect bounded structured diagnostics; do not infer retry or rollback.
var failure = new NativeEngineException("database.postgresql", "commit", 9, 7, -42, 73,
    "The native engine was poisoned. Inspect the transaction outcome and close it.");
Console.WriteLine($"{failure.EngineId}/{failure.Operation}: status {failure.StatusCode}, correlation {failure.CorrelationId}");

// Advanced: a preflight failure is a different category and has no native record.
try
{
    throw new EngineLoadException(EngineLoadFailure.MissingAsset, "database.sqlite", "linux-x64",
        "Restore the package containing the matching Linux x64 native asset.");
}
catch (EngineLoadException error) when (error.Failure == EngineLoadFailure.MissingAsset)
{
    if (error.StatusCode is not null) throw new InvalidOperationException("Pre-native metadata was fabricated.");
    Console.WriteLine($"{error.Failure}: {error.Message}");
}
