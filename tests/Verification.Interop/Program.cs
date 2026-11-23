using System.Runtime.CompilerServices;
using Korda.Verification.Interop;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Actual Native AOT execution required.");
if (args.Length == 2 && args[0] == "--extent") { PropertySuite.ExtentCase(Extent.Parse(args[1])); return 0; }
if (args.Length == 1 && args[0].StartsWith("--child-", StringComparison.Ordinal)) return await ChildCases.Run(args[0]);
PropertySuite.Run();
await RaceSuite.Run();
Console.WriteLine($"PASS A20 {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")} assertions={Check.Assertions} finite-orders=72; seed={PropertySuite.Seed:x16}; generator={PropertySuite.GeneratorVersion}");
return 0;
