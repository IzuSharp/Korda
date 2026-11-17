using System.Runtime.CompilerServices;
using NativeWeave.Analyzers.Example;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Expected actual Native AOT execution.");
if (NativeMethods.Version() != 1) throw new InvalidOperationException("Unexpected existing bootstrap ABI.");
var greeting = AdvancedExample.Greet("Analyzer 世界 🦀");
if (greeting != "Hello, Analyzer 世界 🦀! From Rust.") throw new InvalidOperationException(greeting);
#if AOT_NEGATIVE
AotWarningProbe.Required();
#endif
Console.WriteLine($"A12 consumer passed; dynamicCode={RuntimeFeature.IsDynamicCodeSupported}; {greeting}");

#if AOT_NEGATIVE
internal static class AotWarningProbe
{
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("A12 verifies framework IL3050 remains visible.")]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("A12 verifies framework IL2026 remains visible.")]
    internal static void Required() { }
}
#endif
