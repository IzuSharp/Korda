using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace NativeWeave.Server.Mcp.Generator;

/// <summary>Preserves instance-field metadata for source-known static MCP DTOs and their base classes.</summary>
/// <remarks>This compiler-only component emits BCL trimming annotations and coverage markers, without a runtime codec, registration table or assembly scan.</remarks>
[Generator(LanguageNames.CSharp)]
public sealed class LayoutPreservationGenerator : IIncrementalGenerator
{
    private const string JsonSerializable = "System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string JsonIgnore = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonInclude = "System.Text.Json.Serialization.JsonIncludeAttribute";
    private const string Registry = "NativeWeave.Server.Mcp.McpRegistry";
    private static readonly SymbolDisplayFormat Full = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Discovers declared static JSON roots and concrete MCP tool input/output type arguments.</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var json = context.SyntaxProvider.ForAttributeWithMetadataName(JsonSerializable,
            static (node, _) => node is ClassDeclarationSyntax,
            static (input, _) => (INamedTypeSymbol)input.TargetSymbol).Collect();
        var tools = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is InvocationExpressionSyntax call && Name(call.Expression) == "AddTool",
            static (input, token) =>
            {
                var method = input.SemanticModel.GetSymbolInfo(input.Node, token).Symbol as IMethodSymbol;
                return method is { TypeArguments.Length: 2 } && method.ContainingType.ToDisplayString() == Registry
                    ? method.TypeArguments : ImmutableArray<ITypeSymbol>.Empty;
            }).Where(static types => !types.IsDefaultOrEmpty).Collect();
        context.RegisterSourceOutput(context.CompilationProvider.Combine(json).Combine(tools),
            static (output, input) => Emit(output, input.Left.Left, input.Left.Right, input.Right));
    }

    private static string? Name(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax access => access.Name.Identifier.ValueText,
        _ => null
    };

    private static void Emit(SourceProductionContext output, Compilation compilation,
        ImmutableArray<INamedTypeSymbol> contexts, ImmutableArray<ImmutableArray<ITypeSymbol>> calls)
    {
        var roots = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var context in contexts)
            foreach (var attribute in context.GetAttributes())
                if (attribute.AttributeClass?.ToDisplayString() == JsonSerializable && attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol type)
                    roots.Add(type);
        foreach (var call in calls) foreach (var type in call) roots.Add(type);
        var all = LayoutProof.AllMetadata(compilation);
        var preserved = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var root in roots.OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
            if (LayoutProof.Rebind(all, root) is { } completeRoot) Visit(output, completeRoot, preserved);
        if (preserved.Count == 0) return;

        var direct = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var indirect = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var dependencies = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in preserved)
        {
            output.CancellationToken.ThrowIfCancellationRequested();
            var definition = type.OriginalDefinition;
            string metadata = LayoutProof.MetadataName(definition);
            string assembly = definition.ContainingAssembly.Identity.Name;
            var complete = LayoutProof.Resolve(all, assembly, metadata);
            if (complete is null) continue;
            var proof = LayoutProof.Create(complete);
            string result = ") return (" + proof.Declared + ", " + proof.Reflection + ");\n";
            if (proof.Declared >= 0 && all.IsSymbolAccessibleWithin(definition, all.Assembly))
            {
                var reference = definition.IsGenericType ? definition.ConstructUnboundGenericType() : definition;
                direct[assembly + "|" + metadata] = "        if (definition == typeof(" + reference.ToDisplayString(Full) + ")" + result;
            }
            else if (proof.Declared >= 0) indirect[assembly + "|" + metadata] = "        if (assembly == " + Literal(assembly) + " && name == " + Literal(metadata) + result;
            // typeof retains each accessible closed instantiation. A private
            // nested/base type cannot be named here; the documented string
            // overload retains its field definitions, including closed generic
            // substitutions. That overload uses documentation names (nested
            // dots), unlike the reflection FullName marker (nested pluses).
            // The actual NativeAOT witness covers this branch.
            string target = all.IsSymbolAccessibleWithin(type, all.Assembly)
                ? "typeof(" + type.ToDisplayString(Full) + ")"
                : Literal(DependencyName(definition)) + ", " + Literal(assembly);
            dependencies.Add("    [global::System.Diagnostics.CodeAnalysis.DynamicDependency(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields | global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, " + target + ")]\n");
        }
        var code = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        code.Append("namespace NativeWeave.Server.Mcp.Generated\n{\ninternal static class LayoutPreservation\n{\n");
        code.Append("    internal static int DeclaredInstanceFieldCount(global::System.Type type) => Proof(type).Item1;\n")
            .Append("    internal static int ReflectionFieldCount(global::System.Type type) => Proof(type).Item2;\n")
            .Append("    private static (int, int) Proof(global::System.Type type)\n    {\n")
            .Append("        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;\n");
        foreach (string proof in direct.Values) code.Append(proof);
        if (indirect.Count != 0)
        {
            code.Append("        string? name = definition.FullName;\n        string? assembly = definition.Assembly.GetName().Name;\n");
            foreach (string proof in indirect.Values) code.Append(proof);
        }
        code.Append("        return (-1, -1);\n    }\n");
        foreach (string dependency in dependencies) code.Append(dependency);
        code.Append("    [global::System.Diagnostics.CodeAnalysis.DynamicDependency(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(LayoutPreservation))]\n");
        code.Append("    [global::System.Runtime.CompilerServices.ModuleInitializer]\n    internal static void Preserve() { }\n}\n}\n");
        output.AddSource("NativeWeave.Mcp.Layout.g.cs", SourceText.From(code.ToString(), Encoding.UTF8));
    }

    private static void Visit(SourceProductionContext output, ITypeSymbol root, HashSet<INamedTypeSymbol> preserved)
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<(ITypeSymbol Type, int Depth)>();
        queue.Enqueue((root, 1));
        while (queue.Count != 0)
        {
            output.CancellationToken.ThrowIfCancellationRequested();
            var item = queue.Dequeue();
            var type = item.Type.WithNullableAnnotation(NullableAnnotation.None);
            if (!Closed(type) || !seen.Add(type)) continue;
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration)
            { preserved.Add(enumeration); continue; }
            if (Scalar(type)) continue;
            // The runtime accepts at most 64 JSON types at depth 16. The source
            // walk allows a conservative superset for options such as public
            // fields. Infinite expanding generic shapes cannot grow compilation
            // output without bound; unmarked types always fail closed at runtime.
            if (item.Depth > 32 || seen.Count > 1024) continue;
            if (type is IArrayTypeSymbol array)
            { queue.Enqueue((array.ElementType, item.Depth + 1)); continue; }
            if (type is not INamedTypeSymbol named) continue;
            string generic = named.OriginalDefinition.ToDisplayString();
            if (generic is "System.Nullable<T>" or "System.Collections.Generic.List<T>")
            { queue.Enqueue((named.TypeArguments[0], item.Depth + 1)); continue; }
            if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct) || FrameworkContainer(named)) continue;
            for (var owner = named; owner is not null && owner.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); owner = owner.BaseType)
            {
                if (owner.IsReferenceType) preserved.Add(owner);
                foreach (var member in owner.GetMembers())
                {
                    if (member.IsStatic || Ignored(member)) continue;
                    if (member is IPropertySymbol { IsIndexer: false } property &&
                        (property.GetMethod?.DeclaredAccessibility == Accessibility.Public || property.SetMethod?.DeclaredAccessibility == Accessibility.Public || Included(property)))
                        queue.Enqueue((property.Type, item.Depth + 1));
                    else if (member is IFieldSymbol { IsImplicitlyDeclared: false } field &&
                        (field.DeclaredAccessibility == Accessibility.Public || Included(field)))
                        queue.Enqueue((field.Type, item.Depth + 1));
                }
            }
        }
    }

    private static bool Included(ISymbol member) => member.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == JsonInclude);
    private static bool Ignored(ISymbol member)
    {
        var ignore = member.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == JsonIgnore);
        if (ignore is null) return false;
        // JsonIgnore defaults to Always (1); Never (0) and conditional ignore
        // retain a potential value and therefore remain in this static closure.
        foreach (var argument in ignore.NamedArguments)
            if (argument.Key == "Condition") return argument.Value.Value is int value && value == 1;
        return true;
    }

    private static bool Closed(ITypeSymbol type)
    {
        if (type.TypeKind is TypeKind.TypeParameter or TypeKind.Error or TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer) return false;
        if (type is IArrayTypeSymbol array) return Closed(array.ElementType);
        return type is not INamedTypeSymbol named || !named.IsUnboundGenericType && !named.IsRefLikeType &&
            (named.ContainingType is null || Closed(named.ContainingType)) && named.TypeArguments.All(Closed);
    }

    private static bool Scalar(ITypeSymbol type) => type.TypeKind == TypeKind.Enum || type.SpecialType is not (SpecialType.None or SpecialType.System_Nullable_T) ||
        type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or "System.TimeSpan" or "System.Uri" or "System.Version" ||
        type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte };

    private static bool FrameworkContainer(INamedTypeSymbol type) =>
        type.ToDisplayString() is "System.Text.Json.JsonElement" or "System.Text.Json.JsonDocument" ||
        type.ContainingNamespace.ToDisplayString().StartsWith("System.Text.Json.Nodes", StringComparison.Ordinal) ||
        type.AllInterfaces.Any(static implemented => implemented.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" or "System.Collections.IEnumerable");

    private static string DependencyName(INamedTypeSymbol type) => type.GetDocumentationCommentId()!.Substring(2);
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
}
