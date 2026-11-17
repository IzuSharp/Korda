using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NativeWeave.Analyzers;

if (args.Length == 2 && args[0] == "--api-reference")
    ApiReference.Write(args[1]);
else
    await new Harness().Run();

internal sealed class Harness
{
    private const string Prefix = "using System;\nusing System.Runtime.InteropServices;\nusing System.Runtime.CompilerServices;\nnamespace NativeWeave.Tests;\n";
    private const string Cdecl = "[DllImport(\"nativeweave_fixture\", CallingConvention = CallingConvention.Cdecl)]";
    private const string Uco = "[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]";
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(Compilation).Assembly.Location).Append(typeof(CSharpCompilation).Assembly.Location)
        .Distinct(StringComparer.Ordinal).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    private int cases;
    private int assertions;

    internal async Task Run()
    {
        await Case("fixed-width imports", Prefix + "internal static class N { " + Cdecl + " private static extern uint Call(byte value, nuint length); }");
        await Case("bool ambiguity", Prefix + "internal static class N { " + Cdecl + " private static extern uint Call([|bool|] value); }", "NWABI001");
        await Case("string encoding", Prefix + "internal static class N { " + Cdecl + " private static extern uint Call([|string|] value); }", "NWABI001");
        await Case("character width", Prefix + "internal static class N { " + Cdecl + " private static extern uint Call([|char|] value); }", "NWABI001");
        await Case("byref storage", Prefix + "internal static class N { " + Cdecl + " private static extern uint Call(ref [|uint|] value); }", "NWABI001");
        await Case("wrong convention", Prefix + "internal static class N { [DllImport(\"nativeweave_fixture\")] private static extern uint [|Call|](); }", "NWABI002");
        await Case("public raw pointer", Prefix + "public static class N { " + Cdecl + " public static extern uint [|Call|](); }", "NWOWN001");
        await Case("UCO static callback", Prefix + "internal static class N { " + Uco + " internal static void Callback(nuint cookie, uint status) { } }");
        await Case("UCO managed callback", Prefix + "internal static class N { " + Uco + " internal static void Callback([|string|] value) { } }", "NWABI001");
        await Case("UCO open generic", Prefix + "internal class N<T> { " + Uco + " internal static void [|Callback|](nuint cookie) { } }", "NWABI001");
        await Case("GC transition rejection", Prefix + "internal static class N { " + Cdecl + " [SuppressGCTransition] private static extern uint [|Call|](); }", "NWASYNC001");
        await Case("fixed pointer escapes return", Prefix + "internal static unsafe class N { internal static nint Read(byte[] a) { fixed(byte* p = a) { return [|(nint)p|]; } } }", "NWOWN001");
        await Case("fixed pointer remains scoped", Prefix + "internal static unsafe class N { internal static byte Read(byte[] a) { fixed(byte* p = a) { return *p; } } }");
        await Case("stack span escape", Prefix + "internal static class N { internal static Span<byte> Read() { Span<byte> data = stackalloc byte[4]; return [|data|]; } }", "NWOWN001");
        await Case("heap span remains alive", Prefix + "internal static class N { internal static ReadOnlySpan<byte> Read() { ReadOnlySpan<byte> data = new byte[4]; return data; } }");
        await Case("SafeHandle raw escape", Prefix + "internal static class N { internal static nint Read(SafeHandle owner) => [|owner.DangerousGetHandle()|]; }", "NWOWN001");
        await Case("local callback root", Prefix + "internal static class N { internal delegate void Callback(); internal static void Register() { Callback callback = () => {}; _ = Marshal.GetFunctionPointerForDelegate([|callback|]); } }", "NWASYNC001");
        await Case("persistent callback root", Prefix + "internal static class N { internal delegate void Callback(); private static readonly Callback Root = () => {}; internal static void Register() { _ = Marshal.GetFunctionPointerForDelegate(Root); } }");
        await Case("dynamic generic creation", Prefix + "internal static class N { internal static Type Build(Type generic, Type argument) => [|generic.MakeGenericType(argument)|]; }", "NWAOT001");
        await Case("closed construction", Prefix + "internal static class N { internal static object Build() => new System.Collections.Generic.List<int>(); }");
        var generator = "using Microsoft.CodeAnalysis; namespace NativeWeave.Tools; [Generator] public sealed class G : ISourceGenerator { public void Initialize(GeneratorInitializationContext c) {} public void Execute(GeneratorExecutionContext c) { BODY } }";
        await Case("generator cannot start cargo", generator.Replace("BODY", "[|System.Diagnostics.Process.Start(\"cargo\")|];"), "NWGEN001");
        await Case("generator cannot read ambient files", generator.Replace("BODY", "_ = [|System.IO.File.ReadAllText(\"schema.json\")|];"), "NWGEN001");
        await Case("generator cannot use clock", generator.Replace("BODY", "_ = [|System.DateTime.UtcNow|];"), "NWGEN001");
        await Case("generator explicit inputs", generator.Replace("BODY", "foreach(var input in c.AdditionalFiles) { _ = input.GetText(c.CancellationToken); }"));
        await Case("unrelated application interop is outside scope", "using System.Runtime.InteropServices; namespace ThirdParty; internal static class N { [DllImport(\"other\")] internal static extern bool Call(string x); }");
        await Case("missing schema", Prefix + "internal static class N { " + Cdecl + " private static extern uint [|Call|](); }", "NWABI002", flags: new[] { "NativeWeaveRequireBindingMetadata" });
        await MetadataCases();
        await ApiCases();
        await IncrementalAndCancellation();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { cases, assertions, compiler = typeof(Compilation).Assembly.GetName().Version?.ToString(), result = "passed" }));
    }

    private async Task MetadataCases()
    {
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var source = "// nativeweave-schema-sha256:" + digest + "\n" + Prefix + "internal static unsafe class N { " + Cdecl + " private static extern uint Call(byte* input); internal static void Go(byte[] data) { fixed(byte* p = data) { Call(p); } } }";
        string Metadata(string clean, string borrow, bool copies) => "nativeweave-metadata-v1\nmodule\tfixture\ndigest\t" + digest +
            "\nnamespace\tNativeWeave.Tests\nlibrary\tnativeweave_fixture\nsource_sha256\t" + Sha(clean) +
            "\nexport\tCall\tuint\tbyte*\nparameter\tCall\tinput\tmut:u8\tin\t" + borrow + "\t\t0\t" + (copies ? "1" : "0") + "\t256\n";
        await Case("complete schema parity", source, files: new[] { new TextFile("fixture.nwmetadata", Metadata(source, "call", false)) });
        var retained = source.Replace("Call(p)", "Call([|p|])");
        await Case("retained fixed input", retained, "NWASYNC001", new[] { new TextFile("fixture.nwmetadata", Metadata(source, "retirement", false)) });
        await Case("copied fixed input", source, files: new[] { new TextFile("fixture.nwmetadata", Metadata(source, "retirement", true)) });
        var signature = source.Replace("uint Call", "uint [|Call|]");
        await Case("changed declaration", signature, "NWABI002", new[] { new TextFile("fixture.nwmetadata", Metadata(source, "call", false).Replace("export\tCall\tuint", "export\tCall\tvoid")) });
        await Case("stale source bytes", signature, "NWGEN001", new[] { new TextFile("fixture.nwmetadata", Metadata(source + "\n", "call", false)) });
        var missing = await Analyze(source, new[] { new TextFile("fixture.nwmetadata", Metadata(source, "call", false).Replace("nativeweave-metadata-v1", "nativeweave-metadata-v99")) });
        Check(missing.Length == 1 && missing[0].Id == "NWABI002" && missing[0].Location.GetLineSpan().Path == "fixture.nwmetadata", "unknown schema file diagnostic"); cases++;
        var duplicate = await Analyze(source, new[] { new TextFile("fixture.nwmetadata", Metadata(source, "call", false) + "export\tCall\tuint\tbyte*\n") });
        Check(duplicate.Any(d => d.Id == "NWGEN001" && d.GetMessage().Contains("Conflicting export")), "duplicate export exact ID"); cases++;
        var callback = await Analyze(source, new[] { new TextFile("fixture.nwmetadata", Metadata(source, "call", false) + "callback\ton_done\t0\top\tCall\tCall\n") });
        Check(callback.Any(d => d.Id == "NWASYNC001" && d.GetMessage().Contains("close/drain")), "missing callback retirement"); cases++;
    }

    private async Task ApiCases()
    {
        const string publicSource = "namespace NativeWeave.Api;\n/// <summary>Options.</summary>\n/// <remarks>Immutable values; no native resources, shared freely, no platform restriction, constant-size managed storage.</remarks>\npublic sealed class Options {\n/// <summary>Returns a configured name.</summary>\n/// <param name=\"name\">Optional name.</param>\n/// <returns>The supplied name.</returns>\npublic string? Get(string? name = null) => name;\n}";
        var compilation = Compile(publicSource);
        var type = compilation.GetTypeByMetadataName("NativeWeave.Api.Options")!;
        var baseline = "nativeweave-public-api-v1\n" + type.GetDocumentationCommentId() + "\t" + NativeWeaveAnalyzer.ApiSignature(type) + "\n";
        var method = type.GetMembers("Get").Single();
        baseline += method.GetDocumentationCommentId() + "\t" + NativeWeaveAnalyzer.ApiSignature(method) + "\n";
        await Case("complete XML and exact API", publicSource, files: new[] { new TextFile("surface.nwapi", baseline) }, flags: new[] { "NativeWeaveCheckPublicDocumentation" });
        await Case("nullable tightening", publicSource.Replace("string? Get(string?", "string? [|Get|](string"), "NWAPI001", new[] { new TextFile("surface.nwapi", baseline) });
        await Case("optional default changed", publicSource.Replace("string? Get", "string? [|Get|]").Replace("name = null", "name = \"changed\""), "NWAPI001", new[] { new TextFile("surface.nwapi", baseline) });
        await Case("XML parameter drift", publicSource.Replace("name=\"name\"", "name=\"wrong\"").Replace("string? Get", "string? [|Get|]"), "NWAPI001", flags: new[] { "NativeWeaveCheckPublicDocumentation" });
        await Case("missing public XML", "namespace NativeWeave.Api; public sealed class [|Missing|] {}", "NWAPI001", flags: new[] { "NativeWeaveCheckPublicDocumentation" });
        var missing = await Analyze(publicSource, Array.Empty<TextFile>(), new[] { "NativeWeaveRequireApiBaseline" });
        Check(missing.Length == 1 && missing[0].Id == "NWAPI001" && missing[0].Location == Location.None, "missing baseline"); cases++;
        const string entities = "using System; namespace NativeWeave.Data { [AttributeUsage(AttributeTargets.Class)] internal sealed class WeaveTableAttribute(string name) : Attribute { internal string Name => name; } } namespace App { [NativeWeave.Data.WeaveTable(\"entity\")] internal sealed class [|Entity|]<T> { internal T Value { get; set; } = default!; } }";
        var model = await Analyze(RemoveMarkers(entities).Source);
        Check(model.Count(d => d.Id == "NWMODEL001") == 2, "explicit entity generic and field unsupported"); cases++;
        const string endpoints = "using System; namespace NativeWeave.Server { [AttributeUsage(AttributeTargets.Class)] internal sealed class ServerEndpointsAttribute : Attribute {} [AttributeUsage(AttributeTargets.Method)] internal sealed class ServerRouteAttribute(string method, string pattern) : Attribute { internal string Method => method; internal string Pattern => pattern; } } namespace App { [NativeWeave.Server.ServerEndpoints] internal sealed class Routes { [NativeWeave.Server.ServerRoute(\"GET\", \"/\")] internal void [|Handle|](ref int id) {} } }";
        await Case("endpoint closed static shape", endpoints, "NWMODEL001");
    }

    private async Task IncrementalAndCancellation()
    {
        var bad = Prefix + "internal static class N { " + Cdecl + " private static extern uint Call(bool value); }";
        var tree = CSharpSyntaxTree.ParseText(bad, ParseOptions(), "incremental.cs");
        var compilation = Compile(tree);
        var analyzer = new NativeWeaveAnalyzer();
        var initial = await Diagnostics(compilation, analyzer, Options(), default);
        Check(initial.Single().Id == "NWABI001", "initial edit diagnostic");
        var edited = tree.WithChangedText(SourceText.From(bad.Replace("bool value", "byte value"), Encoding.UTF8));
        var after = compilation.ReplaceSyntaxTree(tree, edited);
        Check((await Diagnostics(after, analyzer, Options(), default)).Length == 0, "correcting one field removes stale diagnostic");
        var unrelated = after.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Other; internal class Unrelated {}", ParseOptions(), "unrelated.cs"));
        Check((await Diagnostics(unrelated, analyzer, Options(), default)).Length == 0, "unrelated edit keeps output");
        var token = new CancellationToken(true);
        try { _ = await Diagnostics(compilation, analyzer, Options(), token); throw new Exception("Pre-cancelled analysis published diagnostics."); }
        catch (OperationCanceledException) { assertions++; }
        using var source = new CancellationTokenSource();
        var cancelling = new CancelText("cancel.nwmetadata", source);
        try { _ = await Diagnostics(compilation, analyzer, Options(new[] { cancelling }), source.Token); throw new Exception("Mid-input cancellation published output."); }
        catch (OperationCanceledException) { assertions++; }
        Check((await Diagnostics(after, analyzer, Options(), default)).Length == 0, "cancelled run does not contaminate next compilation");
        cases += 3;
    }

    private async Task Case(string name, string markup, string? id = null, TextFile[]? files = null, string[]? flags = null)
    {
        var parsed = RemoveMarkers(markup); var actual = await Analyze(parsed.Source, files, flags);
        if (id == null) Check(actual.Length == 0, name + ": expected no diagnostics, actual " + string.Join("; ", actual.Select(d => d.ToString())));
        else
        {
            Check(actual.Length == 1, name + ": expected one " + id + ", actual " + string.Join("; ", actual.Select(d => d.ToString())));
            var diagnostic = actual.Single();
            Check(diagnostic.Id == id, name + ": diagnostic ID");
            Check(diagnostic.Location.SourceSpan == parsed.Span, name + ": exact span " + diagnostic.Location.SourceSpan + " != " + parsed.Span);
            Check(diagnostic.Severity == DiagnosticSeverity.Error, name + ": blocking severity");
            Check(!string.IsNullOrWhiteSpace(diagnostic.GetMessage()) && diagnostic.Descriptor.HelpLinkUri?.EndsWith(id.ToLowerInvariant(), StringComparison.Ordinal) == true, name + ": actionable text/help");
        }
        cases++; Console.WriteLine("PASS " + name);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, TextFile[]? files = null, string[]? flags = null) =>
        await Diagnostics(Compile(source), new NativeWeaveAnalyzer(), Options(files, flags), default);
    private static Task<ImmutableArray<Diagnostic>> Diagnostics(CSharpCompilation compilation, DiagnosticAnalyzer analyzer, AnalyzerOptions options, CancellationToken token) =>
        compilation.WithAnalyzers(ImmutableArray.Create(analyzer), new CompilationWithAnalyzersOptions(options, null, true, false)).GetAnalyzerDiagnosticsAsync(token);
    private static AnalyzerOptions Options(AdditionalText[]? files = null, string[]? flags = null) => new(
        files?.ToImmutableArray() ?? ImmutableArray<AdditionalText>.Empty, new ConfigProvider(flags ?? Array.Empty<string>()));
    private static CSharpParseOptions ParseOptions() => new(LanguageVersion.LatestMajor, DocumentationMode.Diagnose);
    private static CSharpCompilation Compile(string source) => Compile(CSharpSyntaxTree.ParseText(source, ParseOptions(), "fixture.cs", Encoding.UTF8));
    private static CSharpCompilation Compile(SyntaxTree tree) => CSharpCompilation.Create("Fixture", new[] { tree }, References,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
    private static (string Source, TextSpan Span) RemoveMarkers(string markup)
    {
        int start = markup.IndexOf("[|", StringComparison.Ordinal); if (start < 0) return (markup, default);
        int end = markup.IndexOf("|]", start, StringComparison.Ordinal);
        return (markup.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }
    private void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal class TextFile(string path, string text) : AdditionalText
{
    public override string Path => path;
    public override SourceText GetText(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return SourceText.From(text, Encoding.UTF8); }
}
internal sealed class CancelText(string path, CancellationTokenSource source) : TextFile(path, "")
{
    public override SourceText GetText(CancellationToken cancellationToken = default)
    { source.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return SourceText.From(""); }
}
internal sealed class ConfigProvider(string[] flags) : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions values = new Config(flags);
    public override AnalyzerConfigOptions GlobalOptions => values;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => values;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => values;
}
internal sealed class Config(string[] flags) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    { value = "true"; return flags.Any(f => key == "build_property." + f); }
}
