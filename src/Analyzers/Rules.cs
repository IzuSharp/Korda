using Microsoft.CodeAnalysis;

namespace NativeWeave.Analyzers;

internal static class Rules
{
    internal static readonly DiagnosticDescriptor Wire = Create("NWABI001", "Unsupported ABI shape");
    internal static readonly DiagnosticDescriptor Abi = Create("NWABI002", "ABI contract mismatch");
    internal static readonly DiagnosticDescriptor Owner = Create("NWOWN001", "Borrow escapes its owner");
    internal static readonly DiagnosticDescriptor Async = Create("NWASYNC001", "Retained callback or input lacks a lifetime");
    internal static readonly DiagnosticDescriptor Model = Create("NWMODEL001", "Unsupported static model");
    internal static readonly DiagnosticDescriptor Generator = Create("NWGEN001", "Invalid generator input or side effect");
    internal static readonly DiagnosticDescriptor Aot = Create("NWAOT001", "Dynamic construction is unsupported");
    internal static readonly DiagnosticDescriptor Api = Create("NWAPI001", "Public API documentation or baseline mismatch");

    private static DiagnosticDescriptor Create(string id, string title) => new DiagnosticDescriptor(
        id, title, "{0}", "NativeWeave", DiagnosticSeverity.Error, true,
        description: "Reject this statically known hazard before native execution. The absence of a diagnostic is not a lifetime or concurrency proof.",
        helpLinkUri: "https://github.com/IzuSharp/NativeWeave/blob/main/docs/guides/analyzers.md#" + id.ToLowerInvariant());
}
