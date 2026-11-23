using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), sdk = Path.GetFullPath(args[1]), dll = Path.GetFullPath(args[2]);
var references = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").Select(p => MetadataReference.CreateFromFile(p)).ToList();
references.Add(MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(dll)!, "Korda.Interop.Abi.dll")));
var product = MetadataReference.CreateFromFile(dll); references.Add(product);
var compilation = CSharpCompilation.Create("ExecutionApiCheck", references: references, options: new(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.All));
var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(product)!;
var types = new List<INamedTypeSymbol>();
void Walk(INamespaceSymbol ns)
{
    foreach (var child in ns.GetNamespaceMembers()) Walk(child);
    if (ns.ToDisplayString() == "Korda.Interop.Execution") types.AddRange(ns.GetTypeMembers());
}
Walk(assembly.GlobalNamespace);
if (types.Count != 2 || types.Any(t => t.DeclaredAccessibility != Accessibility.Internal)) throw new Exception("A08 must expose no public executor plumbing.");
var lifetime = types.Single(t => t.Name == "ExecutorLifetime");
var result = types.Single(t => t.Name == "ExecutorJoinResult");
if (!lifetime.IsSealed || !result.IsReadOnly || result.TypeKind != TypeKind.Struct) throw new Exception("Internal close owner/result shape changed.");
foreach (string contract in new[] { "System.IDisposable", "System.IAsyncDisposable" })
    if (!lifetime.AllInterfaces.Any(t => t.ToDisplayString() == contract)) throw new Exception("Missing exact close interface: " + contract);
var ctor = lifetime.InstanceConstructors.Single();
if (ctor.DeclaredAccessibility != Accessibility.Internal || ctor.Parameters.Length != 2 ||
    ctor.Parameters[0].Type.ToDisplayString() != "Korda.Interop.Async.AsyncOperation<Korda.Interop.Execution.ExecutorJoinResult>" ||
    ctor.Parameters[1].Type.ToDisplayString() != "System.Func<bool>") throw new Exception("Trusted typed close/scope contract changed.");
var close = lifetime.GetMembers("CloseAsync").OfType<IMethodSymbol>().Single();
if (close.IsStatic || close.IsGenericMethod || close.Parameters.Length != 1 ||
    close.Parameters[0].Type.ToDisplayString() != "System.Threading.CancellationToken" || !close.Parameters[0].HasExplicitDefaultValue ||
    close.ReturnType.ToDisplayString() != "System.Threading.Tasks.Task<Korda.Interop.Execution.ExecutorJoinResult>")
    throw new Exception("CloseAsync must expose only a caller-wait token and a typed Task result.");
var request = lifetime.GetMembers("RequestClose").OfType<IMethodSymbol>().Single();
if (!request.ReturnsVoid || request.Parameters.Length != 0) throw new Exception("RequestClose must be parameterless and nonblocking.");
var dispose = lifetime.GetMembers("DisposeAsync").OfType<IMethodSymbol>().Single();
if (dispose.ReturnType.ToDisplayString() != "System.Threading.Tasks.ValueTask" || dispose.Parameters.Length != 0) throw new Exception("DisposeAsync contract differs.");
var fields = result.GetMembers().OfType<IPropertySymbol>().ToDictionary(p => p.Name, p => p.Type.SpecialType);
if (fields.Count != 5 || fields["Joined"] != SpecialType.System_Boolean ||
    new[] { "WorkersStarted", "WorkersStopped", "OutstandingOperations", "ReservedBytes" }.Any(name => fields[name] != SpecialType.System_UInt64))
    throw new Exception("Join diagnostics must preserve all five fixed observations.");
var xml = XDocument.Load(Path.ChangeExtension(dll, ".xml"));
var docs = xml.Descendants("member").ToDictionary(e => e.Attribute("name")!.Value);
foreach (var type in types)
    if (!docs.TryGetValue(type.GetDocumentationCommentId()!, out var element) || element.Element("summary") is null || element.Element("remarks") is null)
        throw new Exception("Internal ownership and timeout documentation is incomplete.");
foreach (var method in new[] { ctor, request, close, dispose })
    if (!docs.TryGetValue(method.GetDocumentationCommentId()!, out var element) || element.Element("summary") is null)
        throw new Exception("Missing compiled XML method contract.");
var negative = CSharpCompilation.Create("UnrelatedConsumer", [CSharpSyntaxTree.ParseText("class C { Korda.Interop.Execution.ExecutorLifetime? executor; }")], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
if (!negative.GetDiagnostics().Any(d => d.Id == "CS0122")) throw new Exception("Applications can access executor plumbing.");
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Interop.Execution/public-api.expected.json")));
if (baseline.RootElement.GetProperty("symbols").GetArrayLength() != 0) throw new Exception("Unexpected public executor baseline.");
string summary = string.Join(" ", docs[lifetime.GetDocumentationCommentId()!].Element("summary")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
string reference = "# Internal executor close reference\n\nGenerated from the compiled Korda.Core assembly and XML by `tests/Interop.Execution/ApiCheck`.\n\nA08 adds **zero public types or members**. Two namespace types remain internal: the sealed `ExecutorLifetime` and readonly `ExecutorJoinResult`.\n\n**Implementation summary:** " + summary + "\n\n`RequestClose()` is nonblocking and idempotent. `CloseAsync(CancellationToken waitCancellation = default)` returns `Task<ExecutorJoinResult>`; its token cancels only the caller's wait. `DisposeAsync()` returns `ValueTask` and completes after typed worker-join evidence and A07 retirement. The constructor takes the exact typed A07 operation and a trusted owned-callback scope query. An owned callback may request close and is rejected if it attempts to await its own drain.\n\nThe immutable result records Joined, WorkersStarted, WorkersStopped, OutstandingOperations and ReservedBytes. Completion requires actual join, equal positive started/stopped counts and zero operation/byte ownership. An unrelated consumer is rejected with CS0122. No public scheduler or default runtime is introduced.\n";
string path = Path.Combine(root, "src/Interop.Execution/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(path, reference);
else if (File.ReadAllText(path) != reference) throw new Exception("Generated executor reference is stale.");
Console.WriteLine("A08 compiled API check: public symbols=0; internal types=2; exact close/wait/join contracts=passed; unrelated consumer CS0122=passed; XML/reference=passed.");
