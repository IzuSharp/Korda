using NativeWeave.Generators.CSharp;
using NativeWeave.Generators.Model;

if (args.Length is < 6 or > 7)
{
    Console.Error.WriteLine("Usage: NativeWeave.Generators.CSharp <sidecar> <parsed-inventory> <adapter.rs> <linked-symbols> <library-name> <output-directory> [namespace]");
    return 2;
}
try
{
    var output = BindingEmitter.Emit(File.ReadAllBytes(args[0]), File.ReadAllBytes(args[2]), File.ReadAllBytes(args[1]),
        File.ReadAllLines(args[3]).Where(x => x.Length != 0), args[4], args.Length == 7 ? args[6] : null);
    Directory.CreateDirectory(args[5]);
    foreach (var (name, content) in new[] { ("Bindings.g.cs", output.Source), ("Bindings.nwmetadata", output.Metadata) })
    {
        var path = Path.Combine(args[5], name);
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
    }
    Console.WriteLine("PASS deterministic A09-validated C# bindings and metadata.");
    return 0;
}
catch (BindingValidationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
