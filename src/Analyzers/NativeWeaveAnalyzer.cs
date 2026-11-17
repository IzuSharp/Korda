using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NativeWeave.Analyzers;

/// <summary>Reports statically identifiable NativeWeave ABI, lifetime, generation, AOT and API hazards.</summary>
/// <remarks>
/// This compiler plug-in consumes explicit AdditionalFiles and semantic source information. It never
/// loads a native engine, starts a process or accesses the network. Analysis is concurrent and cancellable;
/// per-compilation state is released with that compilation. No application runtime API is introduced.
/// The absence of diagnostics does not prove arbitrary unsafe code or concurrent retirement correct.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NativeWeaveAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Creates the stateless compiler plug-in.</summary>
    /// <remarks>Construction allocates no native resources and performs no input discovery or external work.</remarks>
    public NativeWeaveAnalyzer() { }

    /// <summary>Gets the stable diagnostic descriptors emitted by this compiler plug-in.</summary>
    /// <value>The six D03 diagnostic classes and separate AOT/API extension classes.</value>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        Rules.Wire, Rules.Abi, Rules.Owner, Rules.Async, Rules.Model, Rules.Generator, Rules.Aot, Rules.Api);

    /// <summary>Registers stateless semantic actions and bounded per-compilation input validation.</summary>
    /// <param name="context">The compiler-owned analyzer registration context.</param>
    /// <remarks>Generated declarations are checked as well as handwritten ones. No global symbol graph is retained.</remarks>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(start =>
        {
            var input = Inputs.Read(start.Options, start.CancellationToken);
            var sources = new ConcurrentDictionary<SyntaxTree, string>();
            var exports = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var api = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            start.RegisterSymbolAction(c => AnalyzeMethod(c, input, sources, exports), SymbolKind.Method);
            start.RegisterSymbolAction(c => AnalyzeModel(c, input), SymbolKind.NamedType);
            start.RegisterSymbolAction(c => AnalyzeApi(c, input, api), SymbolKind.NamedType, SymbolKind.Method,
                SymbolKind.Property, SymbolKind.Event, SymbolKind.Field);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, input), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeEscape(c, input), OperationKind.Return, OperationKind.SimpleAssignment);
            start.RegisterOperationAction(c => AnalyzeProperty(c, input), OperationKind.PropertyReference);
            start.RegisterCompilationEndAction(end =>
            {
                end.CancellationToken.ThrowIfCancellationRequested();
                foreach (var diagnostic in input.Diagnostics) end.ReportDiagnostic(diagnostic);
                foreach (var binding in input.Bindings.Values)
                    foreach (var export in binding.Exports.Keys)
                        if (!exports.ContainsKey(binding.Namespace + ":" + export))
                            end.ReportDiagnostic(Diagnostic.Create(Rules.Abi, Location.None,
                                "Metadata export '" + export + "' has no managed import in '" + binding.Namespace + "'; regenerate the complete binding inventory."));
                foreach (var entry in input.Api)
                    if (!api.ContainsKey(entry.Key)) end.ReportDiagnostic(Diagnostic.Create(Rules.Api, Location.None,
                        "Public baseline symbol '" + entry.Key + "' is missing; preserve the declaration or review an explicit compatibility change."));
            });
        });
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, Inputs input,
        ConcurrentDictionary<SyntaxTree, string> sources, ConcurrentDictionary<string, byte> seenExports)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsImplicitlyDeclared || method.PartialDefinitionPart != null || !Owned(method, input)) return;
        var import = Attribute(method, "System.Runtime.InteropServices.LibraryImportAttribute") ??
            Attribute(method, "System.Runtime.InteropServices.DllImportAttribute");
        var callback = Attribute(method, "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute");
        if (import == null && callback == null) return;
        var location = NameLocation(method);
        if (!method.IsStatic || method.IsGenericMethod || GenericContainer(method.ContainingType))
            Report(Rules.Wire, location, "Native entry '" + method.Name + "' must be static and closed/non-generic; use a static nongeneric ABI adapter.");
        if (import != null && Public(method))
            Report(Rules.Owner, location, "Raw import '" + method.Name + "' must be internal; expose an owner-aware typed facade instead of application pointers.");
        if (!Wire(method.ReturnType, 0)) Report(Rules.Wire, TypeLocation(method, -1),
            "Return type '" + method.ReturnType.ToDisplayString() + "' has no explicit wire representation; use fixed-width status and output storage.");
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var parameter = method.Parameters[i];
            if (!Wire(parameter.Type, 0) || parameter.RefKind != RefKind.None)
                Report(Rules.Wire, TypeLocation(method, i), "Parameter '" + parameter.Name + "' uses an unsupported wire shape; pass a fixed-width scalar or explicit unmanaged pointer, with encoding/lifetime in the binding contract.");
        }
        if (!Cdecl(method, import, callback)) Report(Rules.Abi, location,
            "Native entry '" + method.Name + "' has no exact Cdecl convention; declare UnmanagedCallConv/UnmanagedCallersOnly with CallConvCdecl (or DllImport CallingConvention.Cdecl).");
        if (Attribute(method, "System.Runtime.InteropServices.SuppressGCTransitionAttribute") != null ||
            Attribute(method, "System.Runtime.InteropServices.UnmanagedCallConvAttribute")?.NamedArguments.Any(a =>
                a.Key == "CallConvs" && a.Value.Values.Any(v => v.Value is INamedTypeSymbol t && t.Name == "CallConvSuppressGCTransition")) == true)
            Report(Rules.Async, location, "SuppressGCTransition is outside the accepted engine profile; preserve GC transitions for callbacks, blocking, locks and reentrancy.");
        if (import == null) return;
        var ns = method.ContainingNamespace.ToDisplayString();
        if (!input.Bindings.TryGetValue(ns, out var contract))
        {
            if (input.RequireMetadata) Report(Rules.Abi, location, "The required .nwmetadata schema is absent for '" + ns + "'; add the exact A10 maintainer output as an AdditionalFile.");
            return;
        }
        var symbol = EntryPoint(method, import);
        if (!seenExports.TryAdd(ns + ":" + symbol, 0))
            Report(Rules.Generator, location, "Conflicting managed imports claim native export '" + symbol + "'; retain one canonical generated declaration.");
        if (!contract.Exports.TryGetValue(symbol, out var expected))
        { Report(Rules.Abi, location, "Import '" + symbol + "' is absent from the verified native inventory; regenerate both language bindings."); return; }
        if (import.ConstructorArguments.Length == 0 || !Equals(import.ConstructorArguments[0].Value, contract.Library) ||
            WireName(method.ReturnType) != expected.Result ||
            !method.Parameters.Select(p => WireName(p.Type)).SequenceEqual(expected.Parameters))
            Report(Rules.Abi, location, "Import '" + symbol + "' differs from the normalized library/signature; use the exact maintainer-generated declaration.");
        var tree = method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
        if (tree != null)
        {
            var hash = sources.GetOrAdd(tree, _ => Hash(tree.GetText(context.CancellationToken).ToString()));
            if (hash != contract.SourceHash) Report(Rules.Generator, location,
                "Generated C# bytes differ from source_sha256 in .nwmetadata; regenerate stale binding inputs together, do not edit the generated import.");
            var marker = "// nativeweave-schema-sha256:" + contract.Digest;
            if (!tree.GetText(context.CancellationToken).ToString().Contains(marker))
                Report(Rules.Generator, location, "The generated schema digest header is missing or stale; regenerate from the same normalized ABI description.");
        }

        void Report(DiagnosticDescriptor rule, Location where, string reason) => context.ReportDiagnostic(Diagnostic.Create(rule, where, reason));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, Inputs input)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!Owned(context.ContainingSymbol, input)) return;
        var call = (IInvocationOperation)context.Operation;
        var method = call.TargetMethod; var type = method.ContainingType.ToDisplayString();
        if (type == "System.Runtime.InteropServices.Marshal" && method.Name == "GetFunctionPointerForDelegate" && call.Arguments.Length > 0)
        {
            var argument = Unwrap(call.Arguments[0].Value);
            if (argument is not IFieldReferenceOperation field || !field.Field.IsStatic || !field.Field.IsReadOnly)
                Report(Rules.Async, call.Arguments[0].Syntax.GetLocation(), "This delegate callback has no statically persistent root; use a static readonly delegate, a static UCO entry, or a reviewed GCHandle retained through native retirement.");
        }
        if (Dynamic(method)) Report(Rules.Aot, call.Syntax.GetLocation(),
            "Dynamic API '" + type + "." + method.Name + "' is outside the owned AOT profile; supply closed static metadata or a generated serializer/handler. Framework IL2026/IL3050 checks remain enabled.");
        if (Generator(context.ContainingSymbol.ContainingType) && SideEffect(method))
            Report(Rules.Generator, call.Syntax.GetLocation(), "Generator operation '" + type + "." + method.Name + "' performs external or nondeterministic work; use explicit AdditionalTexts/configuration and run native builds in the maintainer tool.");
        var import = Attribute(method, "System.Runtime.InteropServices.LibraryImportAttribute") ?? Attribute(method, "System.Runtime.InteropServices.DllImportAttribute");
        if (import == null || !input.Bindings.TryGetValue(method.ContainingNamespace.ToDisplayString(), out var binding) ||
            !binding.Exports.TryGetValue(EntryPoint(method, import), out var export)) return;
        foreach (var argument in call.Arguments)
        {
            var ordinal = argument.Parameter?.Ordinal ?? -1;
            if (ordinal < 0 || ordinal >= export.Lifetimes.Count) continue;
            var lifetime = export.Lifetimes[ordinal];
            if ((lifetime.BorrowEnd == "retirement" || lifetime.BorrowEnd == "owner") && !lifetime.Copies && FixedValue(argument.Value))
                Report(Rules.Async, argument.Syntax.GetLocation(), "Parameter '" + lifetime.Name + "' outlives this fixed scope; copy before acceptance or retain an explicit owner/pin until native retirement.");
        }

        void Report(DiagnosticDescriptor rule, Location where, string reason) => context.ReportDiagnostic(Diagnostic.Create(rule, where, reason));
    }

    private static void AnalyzeEscape(OperationAnalysisContext context, Inputs input)
    {
        if (!Owned(context.ContainingSymbol, input)) return;
        context.CancellationToken.ThrowIfCancellationRequested();
        IOperation? value = null;
        if (context.Operation is IReturnOperation result) value = result.ReturnedValue;
        if (context.Operation is ISimpleAssignmentOperation assignment &&
            (assignment.Target is IFieldReferenceOperation || assignment.Target is IPropertyReferenceOperation)) value = assignment.Value;
        if (value == null) return;
        if (FixedValue(value) || StackBorrow(value) || value.DescendantsAndSelf().OfType<IInvocationOperation>().Any(x =>
                (x.TargetMethod.Name == "DangerousGetHandle" && Inherits(x.TargetMethod.ContainingType, "System.Runtime.InteropServices.SafeHandle")) ||
                (x.TargetMethod.ContainingType.ToDisplayString() == "System.Runtime.CompilerServices.Unsafe" && x.TargetMethod.Name == "AsPointer")))
            context.ReportDiagnostic(Diagnostic.Create(Rules.Owner, value.Syntax.GetLocation(),
                "A borrowed address escapes through a return or member store; consume it synchronously under an admitted owner lease, or transfer a reviewed owner object instead."));
    }

    private static void AnalyzeProperty(OperationAnalysisContext context, Inputs input)
    {
        if (!Owned(context.ContainingSymbol, input) || !Generator(context.ContainingSymbol.ContainingType)) return;
        var property = ((IPropertyReferenceOperation)context.Operation).Property;
        if ((property.ContainingType.ToDisplayString() == "System.DateTime" || property.ContainingType.ToDisplayString() == "System.DateTimeOffset") &&
            (property.Name == "Now" || property.Name == "UtcNow" || property.Name == "Today"))
            context.ReportDiagnostic(Diagnostic.Create(Rules.Generator, context.Operation.Syntax.GetLocation(),
                "A generator cannot depend on wall-clock time; derive deterministic output only from declared inputs."));
    }

    private static void AnalyzeModel(SymbolAnalysisContext context, Inputs input)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)context.Symbol;
        var entity = Attribute(type, "NativeWeave.Data.WeaveTableAttribute");
        var endpoint = Attribute(type, "NativeWeave.Server.ServerEndpointsAttribute");
        if (entity == null && endpoint == null) return;
        if (GenericContainer(type)) context.ReportDiagnostic(Diagnostic.Create(Rules.Model, NameLocation(type),
            "Static metadata for an open generic type is unsupported; declare a closed nongeneric entity or endpoint group."));
        if (entity != null)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (property.IsStatic || Attribute(property, "NativeWeave.Data.WeaveIgnoreAttribute") != null) continue;
                var column = Attribute(property, "NativeWeave.Data.WeaveColumnAttribute");
                var name = column?.ConstructorArguments.FirstOrDefault().Value as string ?? property.Name;
                if (!names.Add(name)) context.ReportDiagnostic(Diagnostic.Create(Rules.Model, NameLocation(property),
                    "Duplicate mapped field identity '" + name + "'; use distinct declared columns."));
                if (property.IsIndexer || property.Type.IsRefLikeType || property.Type.TypeKind == TypeKind.Pointer ||
                    property.Type.TypeKind == TypeKind.FunctionPointer || property.Type.TypeKind == TypeKind.TypeParameter ||
                    property.Type.SpecialType == SpecialType.System_Object || property.Type.TypeKind == TypeKind.Dynamic)
                    context.ReportDiagnostic(Diagnostic.Create(Rules.Model, NameLocation(property),
                        "Member '" + property.Name + "' has no supported static value shape; use an explicit supported codec or ignore the member. B10 owns complete codec generation."));
            }
        }
        if (endpoint != null)
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                if (Attribute(method, "NativeWeave.Server.ServerRouteAttribute") != null &&
                    (!method.IsStatic || method.IsGenericMethod || method.Parameters.Any(p => p.RefKind != RefKind.None || p.Type.IsRefLikeType)))
                    context.ReportDiagnostic(Diagnostic.Create(Rules.Model, NameLocation(method),
                        "An endpoint handler must be static and closed, without by-reference/ref-like parameters; use the accepted typed handler shape. B15 owns handler generation."));
    }

    private static void AnalyzeApi(SymbolAnalysisContext context, Inputs input, ConcurrentDictionary<string, byte> observed)
    {
        var symbol = context.Symbol;
        if (!Owned(symbol, input) || !Public(symbol) || symbol.IsImplicitlyDeclared ||
            symbol is IMethodSymbol { PartialDefinitionPart: not null } ||
            symbol is IMethodSymbol { AssociatedSymbol: not null }) return;
        context.CancellationToken.ThrowIfCancellationRequested();
        var id = symbol.GetDocumentationCommentId(); if (id == null) return;
        observed.TryAdd(id, 0);
        if (input.HasApiBaseline && (!input.Api.TryGetValue(id, out var expected) || expected != ApiSignature(symbol)))
            context.ReportDiagnostic(Diagnostic.Create(Rules.Api, NameLocation(symbol),
                "Public declaration '" + id + "' differs from the reviewed source/nullability/default baseline; review the compatibility change before updating that baseline."));
        if (input.CheckDocumentation && !Documentation(symbol, context.Compilation, context.CancellationToken, out var reason))
            context.ReportDiagnostic(Diagnostic.Create(Rules.Api, NameLocation(symbol), "Public symbol '" + id + "': " + reason));
    }

    internal static string ApiSignature(ISymbol symbol)
    {
        var format = new SymbolDisplayFormat(globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
            memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers |
                SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType |
                SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue |
                SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeRef,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue |
                SymbolDisplayParameterOptions.IncludeExtensionThis,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
        return symbol.Kind + "|" + symbol.ToDisplayString(format);
    }

    private static bool Documentation(ISymbol symbol, Compilation compilation, CancellationToken cancellation, out string reason)
    {
        reason = "Supply XML summary, exact parameter/type-parameter documentation and return/value semantics.";
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellation);
        if (string.IsNullOrWhiteSpace(xml) || xml!.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        XElement element;
        try { element = XElement.Parse(xml); } catch (System.Xml.XmlException) { return false; }
        var inherit = element.Element("inheritdoc");
        if (inherit != null)
        {
            var cref = (string?)inherit.Attribute("cref");
            ISymbol? inherited = cref == null ? symbol switch
            {
                IMethodSymbol m => m.OverriddenMethod ?? m.ExplicitInterfaceImplementations.FirstOrDefault(),
                IPropertySymbol p => p.OverriddenProperty ?? p.ExplicitInterfaceImplementations.FirstOrDefault(),
                IEventSymbol e => e.OverriddenEvent ?? e.ExplicitInterfaceImplementations.FirstOrDefault(),
                _ => null
            } : DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation);
            if (inherited == null || SymbolEqualityComparer.Default.Equals(inherited, symbol) ||
                string.IsNullOrWhiteSpace(inherited.GetDocumentationCommentXml(cancellationToken: cancellation)))
            { reason = "inheritdoc must resolve to a documented base/interface/explicit cref symbol; use complete local XML when no such symbol exists."; return false; }
            return true;
        }
        if (string.IsNullOrWhiteSpace(element.Element("summary")?.Value)) return false;
        var parameters = symbol is IMethodSymbol method ? method.Parameters.Select(p => p.Name) :
            symbol is IPropertySymbol property ? property.Parameters.Select(p => p.Name) : Enumerable.Empty<string>();
        var generic = symbol is IMethodSymbol genericMethod ? genericMethod.TypeParameters.Select(p => p.Name) :
            symbol is INamedTypeSymbol type ? type.TypeParameters.Select(p => p.Name) : Enumerable.Empty<string>();
        bool Matches(string tag, IEnumerable<string> names) => element.Elements(tag).All(e => !string.IsNullOrWhiteSpace(e.Value)) &&
            element.Elements(tag).Select(e => (string?)e.Attribute("name") ?? "").OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(names.OrderBy(x => x, StringComparer.Ordinal));
        if (!Matches("param", parameters) || !Matches("typeparam", generic)) return false;
        if (symbol is IMethodSymbol m2 && !m2.ReturnsVoid && m2.MethodKind != MethodKind.Constructor && string.IsNullOrWhiteSpace(element.Element("returns")?.Value)) return false;
        if (symbol is IPropertySymbol && string.IsNullOrWhiteSpace(element.Element("value")?.Value)) return false;
        if (symbol is INamedTypeSymbol && string.IsNullOrWhiteSpace(element.Element("remarks")?.Value))
        { reason = "Public types need remarks describing shared lifetime, concurrency, capability/platform and cost policy."; return false; }
        return true;
    }

    private static bool Owned(ISymbol symbol, Inputs input) => input.All ||
        symbol.ContainingNamespace?.ToDisplayString().StartsWith("NativeWeave.", StringComparison.Ordinal) == true;
    private static bool Public(ISymbol symbol) => (symbol.DeclaredAccessibility == Accessibility.Public ||
        symbol.DeclaredAccessibility == Accessibility.Protected || symbol.DeclaredAccessibility == Accessibility.ProtectedOrInternal) &&
        (symbol.ContainingType == null || Public(symbol.ContainingType));
    private static AttributeData? Attribute(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);
    private static bool GenericContainer(INamedTypeSymbol? type) => type != null && (type.Arity != 0 || GenericContainer(type.ContainingType));
    private static string EntryPoint(IMethodSymbol method, AttributeData import) => import.NamedArguments.FirstOrDefault(a => a.Key == "EntryPoint").Value.Value as string ?? method.Name;
    private static bool Inherits(INamedTypeSymbol? type, string fullName) => type != null && (type.ToDisplayString() == fullName || Inherits(type.BaseType, fullName));
    private static bool Generator(INamedTypeSymbol? type) => type != null && (type.AllInterfaces.Any(i =>
        i.ToDisplayString() == "Microsoft.CodeAnalysis.ISourceGenerator" || i.ToDisplayString() == "Microsoft.CodeAnalysis.IIncrementalGenerator") || Generator(type.ContainingType));
    private static IOperation Unwrap(IOperation operation) => operation is IConversionOperation conversion ? Unwrap(conversion.Operand) : operation;
    private static bool FixedValue(IOperation value) =>
        (value.Type is IPointerTypeSymbol || value.Type?.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr) &&
        value.DescendantsAndSelf().OfType<ILocalReferenceOperation>().Any(local =>
        local.Local.DeclaringSyntaxReferences.Any(r => r.GetSyntax().AncestorsAndSelf().Any(n => n is FixedStatementSyntax)));
    private static bool StackBorrow(IOperation value) => value.Type?.IsRefLikeType == true &&
        (value.Syntax.DescendantNodesAndSelf().Any(n => n is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax) ||
         value.DescendantsAndSelf().OfType<ILocalReferenceOperation>().Any(local => local.Local.DeclaringSyntaxReferences.Any(r =>
             r.GetSyntax() is VariableDeclaratorSyntax variable && variable.Initializer?.Value.DescendantNodesAndSelf().Any(n =>
                 n is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax) == true)));
    private static bool Dynamic(IMethodSymbol method)
    {
        var type = method.ContainingType.ToDisplayString();
        return (type == "System.Type" && (method.Name == "GetType" || method.Name == "MakeGenericType")) ||
            (type == "System.Reflection.MethodInfo" && method.Name == "MakeGenericMethod") ||
            (type == "System.Reflection.Assembly" && (method.Name == "Load" || method.Name == "LoadFrom" || method.Name == "GetTypes")) ||
            (type == "System.Activator" && method.Name == "CreateInstance" && !method.IsGenericMethod) ||
            (type.StartsWith("System.Linq.Expressions.Expression", StringComparison.Ordinal) && method.Name == "Compile");
    }
    private static bool SideEffect(IMethodSymbol method)
    {
        var type = method.ContainingType.ToDisplayString();
        return type == "System.Diagnostics.Process" || type == "System.IO.File" || type == "System.IO.Directory" ||
            type.StartsWith("System.Net.", StringComparison.Ordinal) || Inherits(method.ContainingType, "System.Data.Common.DbConnection") ||
            type == "System.Environment" || type == "System.Random" || (type == "System.Guid" && method.Name == "NewGuid");
    }
    private static bool Cdecl(IMethodSymbol method, AttributeData? import, AttributeData? callback)
    {
        if (import?.AttributeClass?.Name == "DllImportAttribute")
            return import.NamedArguments.Any(a => a.Key == "CallingConvention" && Equals(a.Value.Value, 2));
        var attr = callback ?? Attribute(method, "System.Runtime.InteropServices.UnmanagedCallConvAttribute");
        return attr != null && attr.NamedArguments.Any(a => a.Key == "CallConvs" && a.Value.Kind == TypedConstantKind.Array && a.Value.Values.Length == 1 &&
            a.Value.Values.Any(v => v.Value is INamedTypeSymbol t && t.ToDisplayString() == "System.Runtime.CompilerServices.CallConvCdecl"));
    }
    private static bool Wire(ITypeSymbol type, int depth)
    {
        if (depth > 16) return false;
        if (type is IPointerTypeSymbol pointer) return pointer.PointedAtType.SpecialType == SpecialType.System_Void ||
            (!pointer.PointedAtType.IsReferenceType && !pointer.PointedAtType.IsRefLikeType && pointer.PointedAtType is not ITypeParameterSymbol &&
             pointer.PointedAtType.SpecialType is not (SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_Decimal));
        if (type is IFunctionPointerTypeSymbol function) return function.Signature.CallingConvention.ToString() == "CDecl" &&
            Wire(function.Signature.ReturnType, depth + 1) && function.Signature.Parameters.All(p => p.RefKind == RefKind.None && Wire(p.Type, depth + 1));
        if (type.SpecialType is SpecialType.System_Void or SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or
            SpecialType.System_Single or SpecialType.System_Double) return true;
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Struct || named.IsGenericType || named.IsRefLikeType) return false;
        var layout = Attribute(named, "System.Runtime.InteropServices.StructLayoutAttribute");
        return layout?.ConstructorArguments.Length > 0 && Equals(layout.ConstructorArguments[0].Value, 0) &&
            named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).All(f => Wire(f.Type, depth + 1));
    }
    private static string WireName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(" ", "");
    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }
    private static Location NameLocation(ISymbol symbol)
    {
        var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return syntax switch
        {
            BaseTypeDeclarationSyntax t => t.Identifier.GetLocation(),
            DelegateDeclarationSyntax d => d.Identifier.GetLocation(),
            MethodDeclarationSyntax m => m.Identifier.GetLocation(),
            ConstructorDeclarationSyntax c => c.Identifier.GetLocation(),
            PropertyDeclarationSyntax p => p.Identifier.GetLocation(),
            VariableDeclaratorSyntax v => v.Identifier.GetLocation(),
            _ => symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None
        };
    }
    private static Location TypeLocation(IMethodSymbol method, int parameter)
    {
        if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax syntax)
            return parameter < 0 ? syntax.ReturnType.GetLocation() : syntax.ParameterList.Parameters[parameter].Type?.GetLocation() ?? NameLocation(method);
        return NameLocation(method);
    }
}
