using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativeWeave.Server.Mcp.Generator;

// These are conservative metadata upper bounds, not CLR object-size estimates.
// RuntimeHelpers.SizeOf and the admitted runtime layout walk own actual storage.
internal readonly struct LayoutProof(int declared, int reflection)
{
    internal int Declared { get; } = declared;
    internal int Reflection { get; } = reflection;

    internal static Compilation AllMetadata(Compilation compilation) =>
        compilation.WithOptions(compilation.Options.WithMetadataImportOptions(MetadataImportOptions.All));

    internal static INamedTypeSymbol? Resolve(Compilation compilation, string assembly, string metadata)
    {
        if (compilation.Assembly.Identity.Name == assembly) return compilation.Assembly.GetTypeByMetadataName(metadata);
        // Only resolve the already discovered named definition. Never enumerate
        // all types in an assembly, and never trust the default public-only view.
        foreach (var reference in compilation.References)
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol imported && imported.Identity.Name == assembly)
                return imported.GetTypeByMetadataName(metadata);
        return null;
    }

    internal static ITypeSymbol? Rebind(Compilation compilation, ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            var element = Rebind(compilation, array.ElementType);
            return element is null ? null : compilation.CreateArrayTypeSymbol(element, array.Rank);
        }
        if (type is not INamedTypeSymbol named || named.IsUnboundGenericType) return null;
        var definition = named.ContainingType is { } parent
            ? (Rebind(compilation, parent) as INamedTypeSymbol)?.GetTypeMembers(named.Name, named.Arity).SingleOrDefault()
            : Resolve(compilation, named.ContainingAssembly.Identity.Name, MetadataName(named.OriginalDefinition));
        if (definition is null || named.Arity == 0) return definition;
        var arguments = named.TypeArguments.Select(argument => Rebind(compilation, argument)).ToArray();
        return arguments.Any(static argument => argument is null) ? null : definition.Construct(arguments!);
    }

    internal static string MetadataName(INamedTypeSymbol type) => type.ContainingType is { } parent
        ? MetadataName(parent) + "+" + type.MetadataName
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.MetadataName;

    internal static LayoutProof Create(INamedTypeSymbol definition)
    {
        var declared = Members(definition);
        int reflection = 0;
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();
        queue.Enqueue(definition);
        while (queue.Count != 0)
        {
            var owner = queue.Dequeue();
            if (!seen.Add(owner)) continue;
            if (ReferenceAssembly(owner) && !FrameworkOwner(owner)) return new LayoutProof(-1, -1);
            var members = Members(owner);
            // One extra slot also covers an empty base/interface metadata owner.
            reflection = Add(reflection, Add(members.All, 1));
            if (owner.BaseType is { } parent) queue.Enqueue(parent);
            foreach (var implemented in owner.Interfaces) queue.Enqueue(implemented);
        }
        return new LayoutProof(declared.Instance, reflection);
    }

    private static (int Instance, int All) Members(INamedTypeSymbol type)
    {
        if (ReferenceAssembly(type) && FrameworkOwner(type))
            return (0, type.SpecialType == SpecialType.System_Enum ? 2 : 0);
        int instance = type.TypeKind == TypeKind.Enum ? 1 : 0, all = instance;
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol field)
            {
                all = Add(all, 1); if (!field.IsStatic) instance = Add(instance, 1);
            }
            else if (member is IEventSymbol eventSymbol)
            {
                // Roslyn hides a field-like event's backing field even in its
                // All metadata view. Custom-accessor events may overcount one.
                all = Add(all, 1); if (!eventSymbol.IsStatic) instance = Add(instance, 1);
            }
        }
        // The pinned compiler exposes primary captures as fields. Counting all
        // primary parameters again is a conservative guard for late synthesis;
        // uncaptured parameters and records may therefore overcount storage.
        foreach (var syntax in type.DeclaringSyntaxReferences)
            if (syntax.GetSyntax() is TypeDeclarationSyntax { ParameterList: { } parameters })
                foreach (var parameter in parameters.Parameters)
                {
                    all = Add(all, 1); instance = Add(instance, 1);
                }
        return (instance, all);
    }

    private static int Add(int left, int right) => (int)Math.Min(int.MaxValue, (long)left + right);

    private static bool ReferenceAssembly(INamedTypeSymbol type) => type.ContainingAssembly.GetAttributes().Any(static attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");

    // Fixed SDK11/runtime VMR297acf930: Object/ValueType and Enum's four
    // interfaces and IEquatable<T> (used by records) are fieldless; Enum declares EnumSeparatorChar plus CoreCLR's
    // s_underlyingTypes (NativeAOT only needs the former). These
    // framework roots are reference-pack inputs even when ProjectReferences
    // correctly use complete implementation assemblies. Other ref-only owners
    // cannot supply a private-field proof and remain uncovered.
    private static bool FrameworkOwner(INamedTypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum ||
        type.ContainingAssembly.GetTypeByMetadataName("System.Object")?.SpecialType == SpecialType.System_Object &&
        (type.ToDisplayString() is "System.IComparable" or "System.IConvertible" or "System.IFormattable" or "System.ISpanFormattable" ||
         type.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>");
}
