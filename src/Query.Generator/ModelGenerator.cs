using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace NativeWeave.Query.Generator;

/// <summary>Generates static descriptors, typed readers and writers for explicitly attributed input models.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ModelGenerator : IIncrementalGenerator
{
    private const string Prefix = "NativeWeave.Data.";
    private static readonly DiagnosticDescriptor CodecError = Rule("NWQ0101", "Native codec required", "No automatic codec for {0}; use a supported scalar or an explicit handwritten codec policy");
    private static readonly DiagnosticDescriptor NullError = Rule("NWQ0102", "Nullability mapping differs", "Nullable column {0} cannot map to the non-null constructor/key; declare a consistent static policy");
    private static readonly DiagnosticDescriptor ConstructorError = Rule("NWQ0103", "Static construction must be unambiguous", "{0}: {1}; declare one accessible static constructor or writable/init properties");
    private static readonly DiagnosticDescriptor MetadataError = Rule("NWQ0104", "Metadata declaration is inconsistent", "{0}: {1}; choose unique exact names and consistent mapping flags");
    private static readonly DiagnosticDescriptor CapabilityError = Rule("NWQ0105", "Native codec capability is absent", "{0} requires codec {1}, absent from the selected compile-time profile; select a compatible package/profile");
    private static readonly DiagnosticDescriptor InputError = Rule("NWQ0106", "Unsupported source input", "{0}: {1}; use an explicit shared input or handwritten static descriptors");
    private static readonly DiagnosticDescriptor DynamicError = Rule("NWQ0107", "AOT-visible static mapping required", "{0} would require dynamic code or runtime type discovery; supply a closed static mapping");
    private static DiagnosticDescriptor Rule(string id, string title, string message) => new(id, title, message, "NativeWeave.Query", DiagnosticSeverity.Error, true);
    private static readonly SymbolDisplayFormat Full = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Discovers only explicit model attributes and bounded compile-time profile hints.</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "WeaveTableAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (input, _) => (INamedTypeSymbol)input.TargetSymbol).Collect();
        var profiles = context.AnalyzerConfigOptionsProvider.Select(static (options, _) => options.GlobalOptions.TryGetValue("build_property.NativeWeaveEnabledNativeCodecs", out var value) ? value : "");
        var mappings = inputs.Combine(profiles).Select(static (input, cancellation) =>
        {
            var emission = new Emission(cancellation);
            Generate(emission, input.Left, input.Right);
            return emission;
        }).WithComparer(EmissionComparer.Instance).WithTrackingName("StaticMappings");
        context.RegisterSourceOutput(mappings, static (output, emission) =>
        {
            foreach (var diagnostic in emission.Diagnostics) output.ReportDiagnostic(diagnostic);
            foreach (var source in emission.Sources) output.AddSource(source.Name, source.Text);
        });
    }

    private static void Generate(Emission output, ImmutableArray<INamedTypeSymbol> models, string enabled)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? codecs = enabled.Length == 0 ? null : new HashSet<string>(enabled.Split(',').Select(x => x.Trim()), StringComparer.Ordinal);
        foreach (var model in models.OrderBy(x => x.ToDisplayString(), StringComparer.Ordinal))
        {
            output.CancellationToken.ThrowIfCancellationRequested();
            if (model.TypeKind != TypeKind.Class || model.IsStatic || model.IsAbstract || model.IsFileLocal || model.TypeParameters.Length != 0 || model.ContainingType is not null || model.BaseType?.SpecialType != SpecialType.System_Object)
            { Report(output, InputError, model, model.Name, "models must be closed, concrete top-level classes or records with no model base class"); continue; }
            var tables = model.GetAttributes().Where(x => Has(x, "WeaveTableAttribute")).ToArray();
            if (tables.Length != 1) { Report(output, MetadataError, model, model.Name, "a model requires exactly one table declaration"); continue; }
            var table = tables[0];
            string? tableName = table.ConstructorArguments.FirstOrDefault().Value as string;
            string? schema = Named(table, "Schema") as string;
            string descriptor = Named(table, "DescriptorName") as string ?? model.Name + "Mapping";
            string qualified = model.ContainingNamespace.ToDisplayString() + "." + descriptor;
            if (string.IsNullOrWhiteSpace(tableName) || schema is not null && string.IsNullOrWhiteSpace(schema) || !SyntaxFacts.IsValidIdentifier(descriptor) || descriptor is "Table" or "Row" || !names.Add(qualified) || model.ContainingNamespace.GetTypeMembers(descriptor).Length != 0)
            { Report(output, MetadataError, model, model.Name, "invalid/duplicate table, schema or descriptor identifier"); continue; }
            bool valid = true;
            var members = new List<Property>();
            var storeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in model.GetMembers().OfType<IPropertySymbol>().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var attributes = property.GetAttributes();
                bool ignore = attributes.Any(x => Has(x, "WeaveIgnoreAttribute"));
                if (ignore)
                {
                    if (attributes.Any(x => x.AttributeClass?.ContainingNamespace.ToDisplayString() == "NativeWeave.Data" && !Has(x, "WeaveIgnoreAttribute")))
                    { Report(output, MetadataError, property, property.Name, "Ignore cannot be combined with mapping annotations"); valid = false; }
                    continue;
                }
                if (property.IsStatic || property.DeclaredAccessibility != Accessibility.Public) continue;
                if (property.IsIndexer || property.GetMethod?.DeclaredAccessibility != Accessibility.Public || property.RefKind != RefKind.None)
                { Report(output, InputError, property, property.Name, "mapped properties require a public ordinary getter"); valid = false; continue; }
                var column = attributes.FirstOrDefault(x => Has(x, "WeaveColumnAttribute"));
                string store = column?.ConstructorArguments.FirstOrDefault().Value as string ?? property.Name;
                bool key = attributes.Any(x => Has(x, "WeaveKeyAttribute"));
                bool version = attributes.Any(x => Has(x, "WeaveVersionAttribute"));
                bool generated = column is not null && Named(column, "Generated") is true;
                if (string.IsNullOrWhiteSpace(store) || !storeNames.Add(store) || property.Name is "Table" or "Row" || property.Name == descriptor)
                { Report(output, MetadataError, property, property.Name, "duplicate/invalid store name or reserved descriptor member"); valid = false; continue; }
                if (Dynamic(property.Type))
                { Report(output, DynamicError, property, property.Name); valid = false; continue; }
                var codec = Codec(property.Type);
                if (codec is null)
                { Report(output, CodecError, property, property.Type.ToDisplayString()); valid = false; continue; }
                bool nullable = IsNullable(property.Type);
                if (key && nullable)
                { Report(output, NullError, property, property.Name); valid = false; continue; }
                if (codecs is not null && !codecs.Contains(codec.Value.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                { Report(output, CapabilityError, property, property.Name, codec.Value.Id); valid = false; continue; }
                members.Add(new Property(property, store, codec.Value.Expression, key, generated, version, nullable));
            }
            if (!valid) continue;
            if (members.Count == 0)
            { Report(output, MetadataError, model, model.Name, "at least one supported mapped property is required"); continue; }
            var constructors = model.InstanceConstructors.Where(x => x.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
                .Where(c => c.Parameters.All(p => p.RefKind == RefKind.None && members.Count(m => string.Equals(m.Symbol.Name, p.Name, StringComparison.OrdinalIgnoreCase) && SymbolEqualityComparer.Default.Equals(m.Symbol.Type, p.Type)) == 1))
                .Where(c => members.All(m => c.Parameters.Any(p => string.Equals(m.Symbol.Name, p.Name, StringComparison.OrdinalIgnoreCase)) || m.Symbol.SetMethod?.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)).ToArray();
            if (constructors.Length != 1)
            { Report(output, ConstructorError, model, model.Name, "no unique accessible constructor covers the mapped properties"); continue; }
            var constructor = constructors[0];
            var ordered = constructor.Parameters.Select(p => members.Single(m => string.Equals(m.Symbol.Name, p.Name, StringComparison.OrdinalIgnoreCase))).Concat(members.Where(m => !constructor.Parameters.Any(p => string.Equals(m.Symbol.Name, p.Name, StringComparison.OrdinalIgnoreCase)))).ToArray();
            foreach (var parameter in constructor.Parameters)
            {
                var member = ordered.Single(m => string.Equals(m.Symbol.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (member.Nullable && !IsNullable(parameter.Type)) { Report(output, NullError, parameter, member.Symbol.Name); valid = false; }
            }
            if (model.GetMembers().OfType<IPropertySymbol>().Any(p => p.IsRequired && !members.Any(m => SymbolEqualityComparer.Default.Equals(m.Symbol, p))) || model.GetMembers().OfType<IFieldSymbol>().Any(f => f.IsRequired))
            { Report(output, ConstructorError, model, model.Name, "an ignored or field-based required member cannot be assigned by the static mapper"); valid = false; }
            if (!valid) continue;
            Emit(output, model, descriptor, tableName!, schema, constructor, ordered);
        }
    }

    private static void Emit(Emission output, INamedTypeSymbol model, string descriptor, string table, string? schema, IMethodSymbol constructor, Property[] properties)
    {
        string entity = model.ToDisplayString(Full);
        string descriptorType = (model.ContainingNamespace.IsGlobalNamespace ? "global::" : "global::" + model.ContainingNamespace.ToDisplayString() + ".") + "@" + descriptor;
        string access = model.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!model.ContainingNamespace.IsGlobalNamespace) source.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).Append(";\n");
        source.Append("/// <summary>Static generated descriptor; native assets and runtime behavior are identical to the handwritten TableBuilder path.</summary>\n")
            .Append(access).Append(" static class @").Append(descriptor).Append("\n{\n")
            .Append("    /// <summary>Immutable table metadata, registered lazily by each datasource.</summary>\n    public static readonly global::NativeWeave.Data.Table<").Append(entity).Append("> Table;\n")
            .Append("    /// <summary>Direct typed row materializer and exact result shape.</summary>\n    public static global::NativeWeave.Data.Projection<").Append(entity).Append("> Row => Table.Row;\n");
        foreach (var property in properties)
            source.Append("    /// <summary>Typed column token for the exact mapped property.</summary>\n    public static readonly global::NativeWeave.Data.Column<").Append(entity).Append(", ").Append(property.Symbol.Type.ToDisplayString(Full)).Append("> @").Append(property.Symbol.Name).Append(";\n");
        source.Append("    static @").Append(descriptor).Append("()\n    {\n        var builder = new global::NativeWeave.Data.TableBuilder<").Append(entity).Append(">(").Append(Literal(table)).Append(", ").Append(schema is null ? "null" : Literal(schema)).Append(");\n");
        foreach (var property in properties)
            source.Append("        ").Append(descriptorType).Append(".@").Append(property.Symbol.Name).Append(" = builder.Column(").Append(Literal(property.Store)).Append(", ").Append(property.Codec).Append(", new global::NativeWeave.Data.ColumnOptions { IsPrimaryKey = ").Append(Bool(property.Key)).Append(", IsGenerated = ").Append(Bool(property.Generated)).Append(", IsConcurrencyToken = ").Append(Bool(property.Version)).Append(" });\n");
        source.Append("        Table = builder.Build(static row => new ").Append(entity).Append('(');
        source.Append(string.Join(", ", constructor.Parameters.Select(p => "row.Get(" + descriptorType + ".@" + properties.Single(m => string.Equals(m.Symbol.Name, p.Name, StringComparison.OrdinalIgnoreCase)).Symbol.Name + ")"))).Append(')');
        var remaining = properties.Where(p => !constructor.Parameters.Any(c => string.Equals(c.Name, p.Symbol.Name, StringComparison.OrdinalIgnoreCase)) || p.Symbol.IsRequired).ToArray();
        if (remaining.Length != 0) source.Append(" { ").Append(string.Join(", ", remaining.Select(p => "@" + p.Symbol.Name + " = row.Get(" + descriptorType + ".@" + p.Symbol.Name + ")"))).Append(" }");
        source.Append(", static (row, value) =>\n        {\n");
        foreach (var property in properties) source.Append("            row.Set(").Append(descriptorType).Append(".@").Append(property.Symbol.Name).Append(", value.@").Append(property.Symbol.Name).Append(");\n");
        source.Append("        });\n    }\n}\n");
        using var hash = SHA256.Create();
        string identity = string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(model.ToDisplayString(Full))).Select(x => x.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        output.AddSource(model.Name + "." + identity + ".Mapping.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static (int Id, string Expression)? Codec(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        { var inner = Codec(n.TypeArguments[0]); return inner is null ? null : (inner.Value.Id, "global::NativeWeave.Data.Codecs.Nullable(" + inner.Value.Expression + ")"); }
        string? name = type.SpecialType switch { SpecialType.System_Int16 => "Int16", SpecialType.System_Int32 => "Int32", SpecialType.System_Int64 => "Int64", SpecialType.System_Boolean => "Boolean", SpecialType.System_String => "String", SpecialType.System_Double => "Double", SpecialType.System_Single => "Single", _ => null };
        int id = type.SpecialType switch { SpecialType.System_Int16 => 1, SpecialType.System_Int32 => 2, SpecialType.System_Int64 => 3, SpecialType.System_Boolean => 4, SpecialType.System_String => 5, SpecialType.System_Double => 11, SpecialType.System_Single => 12, _ => 0 };
        if (type is IArrayTypeSymbol array && array.Rank == 1 && array.ElementType.SpecialType == SpecialType.System_Byte) { name = "Bytes"; id = 6; }
        if (name is null) (name, id) = type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() switch { "System.Guid" => ("Guid", 7), "System.DateOnly" => ("Date", 8), "System.TimeOnly" => ("Time", 9), "System.DateTimeOffset" => ("UtcInstant", 10), "System.DateTime" => ("UtcDateTime", 10), _ => (null, 0) };
        if (type.TypeKind == TypeKind.Enum) return (17, "global::NativeWeave.Data.Codecs.EnumInt32<" + type.ToDisplayString(Full) + ">()");
        if (name is null) return null;
        string expression = "global::NativeWeave.Data.Codecs." + name;
        if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated) expression = "global::NativeWeave.Data.Codecs.NullableReference(" + expression + ")";
        return (id, expression);
    }
    private static bool Dynamic(ITypeSymbol type) => type.TypeKind is TypeKind.Dynamic or TypeKind.TypeParameter or TypeKind.Delegate || type.SpecialType == SpecialType.System_Object || type.ToDisplayString() is "System.Type" or "System.Reflection.MemberInfo" || type is IArrayTypeSymbol array && Dynamic(array.ElementType) || type is INamedTypeSymbol n && n.TypeArguments.Any(Dynamic);
    private static bool IsNullable(ITypeSymbol type) => type.NullableAnnotation == NullableAnnotation.Annotated || type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    private static bool Has(AttributeData attribute, string name) => attribute.AttributeClass?.ToDisplayString() == Prefix + name;
    private static object? Named(AttributeData attribute, string name) => attribute.NamedArguments.FirstOrDefault(p => p.Key == name).Value.Value;
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
    private static string Bool(bool value) => value ? "true" : "false";
    private static void Report(Emission output, DiagnosticDescriptor descriptor, ISymbol symbol, params object[] args) => output.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), args));
    // Compare emitted semantic content after symbol resolution. Unrelated compilation
    // edits then reuse the output stage; diagnostics retain fresh source-tree locations.
    private sealed class Emission(CancellationToken cancellation)
    {
        internal CancellationToken CancellationToken { get; } = cancellation;
        internal List<Diagnostic> Diagnostics { get; } = [];
        internal List<(string Name, SourceText Text)> Sources { get; } = [];
        internal void ReportDiagnostic(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);
        internal void AddSource(string name, SourceText source) => Sources.Add((name, source));
    }
    private sealed class EmissionComparer : IEqualityComparer<Emission>
    {
        internal static readonly EmissionComparer Instance = new();
        public bool Equals(Emission? first, Emission? second) => ReferenceEquals(first, second) || first is not null && second is not null &&
            first.Diagnostics.Count == 0 && second.Diagnostics.Count == 0 && first.Sources.Count == second.Sources.Count &&
            first.Sources.Zip(second.Sources, (a, b) => a.Name == b.Name && a.Text.ContentEquals(b.Text)).All(x => x);
        public int GetHashCode(Emission value) => value.Sources.Count;
    }
    private sealed class Property(IPropertySymbol symbol, string store, string codec, bool key, bool generated, bool version, bool nullable)
    {
        internal IPropertySymbol Symbol { get; } = symbol;
        internal string Store { get; } = store;
        internal string Codec { get; } = codec;
        internal bool Key { get; } = key;
        internal bool Generated { get; } = generated;
        internal bool Version { get; } = version;
        internal bool Nullable { get; } = nullable;
    }
}
