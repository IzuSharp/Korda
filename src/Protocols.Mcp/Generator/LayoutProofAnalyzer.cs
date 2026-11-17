using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NativeWeave.Server.Mcp.Generator;

/// <summary>Rejects a final compilation whose generated DTO fields exceed its static metadata proof.</summary>
/// <remarks>The check includes other generators' output and inspects only the finite definitions named by the emitted proof.</remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LayoutProofAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Invalid = new("NWMCP001", "MCP field metadata proof is stale",
        "{0}: the final compilation exceeds the generated MCP field metadata proof ({1}); compile the complete DTO definition before generating its layout proof",
        "NativeWeave.Server.Mcp", DiagnosticSeverity.Error, true, customTags: [WellKnownDiagnosticTags.NotConfigurable]);

    /// <summary>Gets the non-suppressible internal layout invariant diagnostic.</summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Invalid];

    /// <summary>Validates the emitted finite proof after all source generators have completed.</summary>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSemanticModelAction(static analysis =>
        {
            if (!analysis.SemanticModel.SyntaxTree.FilePath.EndsWith("NativeWeave.Mcp.Layout.g.cs", StringComparison.Ordinal)) return;
            var compilation = analysis.SemanticModel.Compilation;
            var helper = compilation.GetTypeByMetadataName("NativeWeave.Server.Mcp.Generated.LayoutPreservation");
            if (helper is null) return;
            var all = LayoutProof.AllMetadata(compilation);
            var bodies = helper.GetMembers().OfType<IMethodSymbol>().Where(static method => method.Name is "DirectProof" or "PrivateProof")
                .Select(method => method.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax(analysis.CancellationToken)).OfType<MethodDeclarationSyntax>()
                .Where(method => method.SyntaxTree == analysis.SemanticModel.SyntaxTree).Select(static method => method.Body!);
            foreach (var statement in bodies.SelectMany(static body => body.Statements).OfType<IfStatementSyntax>())
            {
                analysis.CancellationToken.ThrowIfCancellationRequested();
                if (statement.Statement is not ReturnStatementSyntax { Expression: TupleExpressionSyntax tuple } || tuple.Arguments.Count != 2) continue;
                string assemblyName, metadata;
                if (statement.Condition is BinaryExpressionSyntax { Right: TypeOfExpressionSyntax reference } &&
                    analysis.SemanticModel.GetTypeInfo(reference.Type, analysis.CancellationToken).Type is INamedTypeSymbol named)
                {
                    assemblyName = named.ContainingAssembly.Identity.Name;
                    metadata = LayoutProof.MetadataName(named.OriginalDefinition);
                }
                else if (statement.Condition is BinaryExpressionSyntax { Right: InvocationExpressionSyntax
                    { Expression: MemberAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: TypeOfExpressionSyntax anchor } }, ArgumentList.Arguments: var arguments } } &&
                    arguments.Count == 3 && arguments[0].Expression is LiteralExpressionSyntax name &&
                    analysis.SemanticModel.GetTypeInfo(anchor.Type, analysis.CancellationToken).Type is INamedTypeSymbol owner)
                { assemblyName = owner.ContainingAssembly.Identity.Name; metadata = name.Token.ValueText; }
                else continue;
                var type = LayoutProof.Resolve(all, assemblyName, metadata);
                int[] expected = tuple.Arguments.Select(static item => int.Parse(((LiteralExpressionSyntax)item.Expression).Token.ValueText, CultureInfo.InvariantCulture)).ToArray();
                var actual = type is null ? new LayoutProof(int.MaxValue, int.MaxValue) : LayoutProof.Create(type);
                if (actual.Declared >= 0 && actual.Declared <= expected[0] && actual.Reflection <= expected[1]) continue;
                string detail = $"declared {actual.Declared}/{expected[0]}, reflection {actual.Reflection}/{expected[1]}";
                analysis.ReportDiagnostic(Diagnostic.Create(Invalid, type?.Locations.FirstOrDefault(static location => location.IsInSource) ?? Location.None,
                    metadata, detail));
            }
        });
    }
}
