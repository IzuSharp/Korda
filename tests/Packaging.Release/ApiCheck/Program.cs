using System.Reflection;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using NativeWeave.Packaging;

if (args.Length != 1) throw new ArgumentException("Supply the generated reference output path.");
Type type = typeof(PackageClosure);
Type[] exported = type.Assembly.GetExportedTypes();
if (exported.Length != 1 || exported[0] != type || !type.IsSealed || type.BaseType != typeof(Microsoft.Build.Utilities.Task))
    throw new InvalidOperationException("Accidental public task type or changed compiler-host inheritance.");
Dictionary<string, Type> expected = new(StringComparer.Ordinal)
{
    ["ClosureFiles"] = typeof(ITaskItem[]),
    ["AssetsFile"] = typeof(string),
    ["RuntimeIdentifier"] = typeof(string),
    ["Mode"] = typeof(string),
    ["ResolvedFiles"] = typeof(ITaskItem[]),
    ["OutputDirectory"] = typeof(string),
    ["Records"] = typeof(ITaskItem[])
};
var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
if (properties.Length != expected.Count || properties.Any(p => !expected.TryGetValue(p.Name, out Type? declared) || p.PropertyType != declared ||
    p.GetMethod is not { IsPublic: true } || (p.Name == "Records" ? p.SetMethod is not { IsPrivate: true } : p.SetMethod is not { IsPublic: true })))
    throw new InvalidOperationException("Compiler-host property signature baseline changed.");
var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(x => !x.IsSpecialName).ToArray();
if (methods.Length != 1 || methods[0].Name != "Execute" || methods[0].ReturnType != typeof(bool) || methods[0].GetParameters().Length != 0 ||
    type.GetConstructors().Length != 1 || type.GetConstructors()[0].GetParameters().Length != 0)
    throw new InvalidOperationException("Compiler-host method/constructor baseline changed.");
if (!properties.Single(x => x.Name == "ClosureFiles").IsDefined(typeof(RequiredAttribute)) ||
    !properties.Single(x => x.Name == "AssetsFile").IsDefined(typeof(RequiredAttribute)) ||
    !properties.Single(x => x.Name == "Records").IsDefined(typeof(OutputAttribute)))
    throw new InvalidOperationException("Required/output MSBuild semantics changed.");
var xml = XDocument.Load(Path.ChangeExtension(type.Assembly.Location, ".xml")).Root!.Element("members")!.Elements("member")
    .ToDictionary(x => (string)x.Attribute("name")!, StringComparer.Ordinal);
string name = type.FullName!;
var symbols = new SortedDictionary<string, string>(StringComparer.Ordinal)
{
    ["T:" + name] = $"public sealed class {name} : {type.BaseType!.FullName}",
    ["M:" + name + ".#ctor"] = $"public {type.Name}()",
    ["M:" + name + ".Execute"] = "public override bool Execute()"
};
foreach (var property in properties)
{
    string propertyType = property.PropertyType == typeof(string) ? "string" : "Microsoft.Build.Framework.ITaskItem[]";
    string attributes = (property.IsDefined(typeof(RequiredAttribute)) ? "[Required] " : "") + (property.IsDefined(typeof(OutputAttribute)) ? "[Output] " : "");
    symbols["P:" + name + "." + property.Name] = attributes + "public " + propertyType + " " + property.Name + " { get; " +
        (property.Name == "Records" ? "private set;" : "set;") + " }";
}
if (xml.Count != symbols.Count || symbols.Keys.Any(id => !xml.ContainsKey(id)))
    throw new InvalidOperationException("XML/compiler public symbol sets differ.");
var lines = new List<string> { "# Packaging compiler-host task reference", "", "Generated from the actual compiled assembly and XML by `tests/Packaging.Release/ApiCheck`. This MSBuild host seam is shipped under `tools/net11.0`; it is absent from application runtime/AOT output. Application APIs remain owned by Core.", "" };
foreach (var (id, signature) in symbols)
{
    XElement member = xml[id];
    if (string.IsNullOrWhiteSpace(member.Element("summary")?.Value) || id.StartsWith("P:", StringComparison.Ordinal) && member.Element("value") == null ||
        id.EndsWith(".Execute", StringComparison.Ordinal) && member.Element("returns") == null)
        throw new InvalidOperationException("Required XML is missing: " + id);
    lines.Add("## `" + id + "`"); lines.Add(""); lines.Add("```csharp"); lines.Add(signature); lines.Add("```"); lines.Add("");
    foreach (XElement child in member.Elements())
    {
        lines.Add("**" + child.Name.LocalName + ":** " + string.Join(" ", child.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        lines.Add("");
    }
}
File.WriteAllText(args[0], string.Join("\n", lines));
Console.WriteLine($"PASS compiled host API/XML: {symbols.Count} symbols; no application public API added.");
