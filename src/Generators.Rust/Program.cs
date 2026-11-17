using NativeWeave.Generators.Model;
using NativeWeave.Generators.Rust;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: NativeWeave.Generators.Rust <sidecar.json> <parsed-inventory.json> <adapter.rs> <linked-symbols.txt> <output-directory>");
    return 2;
}
try
{
    var model = BindingReader.Read(File.ReadAllBytes(args[0]), args[0]);
    var output = ExportEmitter.Emit(model, File.ReadAllBytes(args[2]), File.ReadAllBytes(args[1]),
        File.ReadAllLines(args[3]).Where(x => x.Length != 0));
    Directory.CreateDirectory(args[4]);
    // All validation and generation complete before publishing any output.
    foreach (var (name, content) in new[] { ("nativeweave.generated.h", output.Header), ("layout_assertions.rs", output.Assertions), ("exports.manifest.json", output.Manifest) })
    {
        var path = Path.Combine(args[4], name);
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
    }
    Console.WriteLine($"PASS {model.Module}; {model.Symbols.Length} exports; schema {model.Digest}");
    return 0;
}
catch (BindingValidationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
