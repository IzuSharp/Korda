using System.Runtime.CompilerServices;
using NativeWeave.Verification.Interop;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Actual Native AOT execution required.");

// Internal maintainer example, not a new application API or package.
// Numeric-only descriptors can contain arbitrary bit patterns without pointer access.
PropertySuite.ExtentCase(new Extent(8, 16, 2, 8, 0, 16, 16, 8));
PropertySuite.ExtentCase(new Extent(ulong.MaxValue, 1, 1, 1, 0, 1, 1, 1));
// Advanced deterministic profile uses all actual C/C++/C# oracles and ownership faults.
if (args.Contains("--advanced")) PropertySuite.Run(256);
Console.WriteLine($"PASS A20 maintainer sample; checks={Check.Assertions}; no arbitrary pointer dereference");
