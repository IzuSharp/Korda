using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NativeWeave.Analyzers;

internal sealed class ParameterContract
{
    internal readonly string Name;
    internal readonly string BorrowEnd;
    internal readonly bool Copies;
    internal ParameterContract(string name, string borrowEnd, bool copies)
    { Name = name; BorrowEnd = borrowEnd; Copies = copies; }
}

internal sealed class ExportContract
{
    internal readonly string Result;
    internal readonly string[] Parameters;
    internal readonly List<ParameterContract> Lifetimes = new List<ParameterContract>();
    internal ExportContract(string result, string[] parameters) { Result = result; Parameters = parameters; }
}

internal sealed class BindingContract
{
    internal string Namespace = "";
    internal string Digest = "";
    internal string SourceHash = "";
    internal string Library = "";
    internal readonly Dictionary<string, ExportContract> Exports = new Dictionary<string, ExportContract>(StringComparer.Ordinal);
}

/// <summary>Per-compilation bounded value data; it never stores a Compilation or symbol globally.</summary>
internal sealed class Inputs
{
    internal readonly Dictionary<string, BindingContract> Bindings = new Dictionary<string, BindingContract>(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> Api = new Dictionary<string, string>(StringComparer.Ordinal);
    internal readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
    internal bool HasApiBaseline;
    internal bool All;
    internal bool RequireMetadata;
    internal bool CheckDocumentation;
    internal bool RequireApi;

    internal static Inputs Read(AnalyzerOptions options, CancellationToken cancellation)
    {
        var input = new Inputs
        {
            All = Flag(options, "NativeWeaveAnalyzeAll"),
            RequireMetadata = Flag(options, "NativeWeaveRequireBindingMetadata"),
            CheckDocumentation = Flag(options, "NativeWeaveCheckPublicDocumentation"),
            RequireApi = Flag(options, "NativeWeaveRequireApiBaseline")
        };
        foreach (var file in options.AdditionalFiles.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            bool binding = file.Path.EndsWith(".nwmetadata", StringComparison.OrdinalIgnoreCase);
            bool api = file.Path.EndsWith(".nwapi", StringComparison.OrdinalIgnoreCase);
            if (!binding && !api) continue;
            var text = file.GetText(cancellation);
            if (text == null || text.Length > 2 * 1024 * 1024)
            {
                input.Diagnostics.Add(Diagnostic.Create(Rules.Abi, Location.None,
                    "Metadata '" + file.Path + "' is unreadable or exceeds 2 MiB; regenerate the bounded maintainer input."));
                continue;
            }
            if (binding) input.ReadBinding(file.Path, text, cancellation);
            else input.ReadApi(file.Path, text, cancellation);
        }
        if (input.RequireApi && !input.HasApiBaseline)
            input.Diagnostics.Add(Diagnostic.Create(Rules.Api, Location.None,
                "The required .nwapi baseline is missing; supply the reviewed public declaration baseline as an AdditionalFile."));
        return input;
    }

    private static bool Flag(AnalyzerOptions options, string name) =>
        options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property." + name, out var value) &&
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private void ReadBinding(string path, SourceText text, CancellationToken cancellation)
    {
        var contract = new BindingContract();
        var headers = new HashSet<string>(StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var callbacks = new List<(string Close, string Drain, TextLine Line)>();
        var owners = new List<(string Kind, string Release, string Parent, TextLine Line)>();
        var valid = true;
        if (text.Lines.Count == 0 || text.Lines[0].ToString() != "nativeweave-metadata-v1")
        { Error(Rules.Abi, text.Lines[0], "Unknown metadata schema; regenerate with the matching A10 maintainer tool."); return; }
        foreach (var line in text.Lines.Skip(1))
        {
            cancellation.ThrowIfCancellationRequested();
            var value = line.ToString(); if (value.Length == 0) continue;
            if (value.Length > 8192) { Error(Rules.Abi, line, "Metadata row exceeds the 8192-character limit."); continue; }
            var cells = value.Split('\t');
            bool Count(int count) { if (cells.Length == count) return true; Error(Rules.Abi, line, "Invalid metadata row width; regenerate the complete input."); return false; }
            switch (cells[0])
            {
                case "module":
                case "digest":
                case "namespace":
                case "library":
                case "source_sha256":
                    if (!Count(2)) break;
                    if (!headers.Add(cells[0]) || cells[1].Length == 0)
                    { Error(Rules.Generator, line, "Duplicate or empty metadata header; use one canonical value."); break; }
                    if (cells[0] == "digest") contract.Digest = cells[1];
                    if (cells[0] == "namespace") contract.Namespace = cells[1].Replace("@", "");
                    if (cells[0] == "library") contract.Library = cells[1];
                    if (cells[0] == "source_sha256") contract.SourceHash = cells[1];
                    break;
                case "export":
                    if (!Count(4)) break;
                    if (!identifiers.Add("export:" + cells[1]))
                    { Error(Rules.Generator, line, "Conflicting export ID '" + cells[1] + "'; regenerate unique exact symbols."); break; }
                    contract.Exports.Add(cells[1], new ExportContract(cells[2].Replace("@", ""), cells[3].Length == 0 ? Array.Empty<string>() : cells[3].Replace("@", "").Split(',')));
                    break;
                case "parameter":
                    if (!Count(10)) break;
                    if (!contract.Exports.TryGetValue(cells[1], out var export))
                    { Error(Rules.Abi, line, "Parameter names an absent export; provide the complete ordered metadata."); break; }
                    if (!identifiers.Add("parameter:" + cells[1] + ":" + cells[2]) ||
                        (cells[7] != "0" && cells[7] != "1") || (cells[8] != "0" && cells[8] != "1") ||
                        !uint.TryParse(cells[9], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    { Error(Rules.Abi, line, "Duplicate parameter or invalid finite lifetime flags; regenerate from A09."); break; }
                    export.Lifetimes.Add(new ParameterContract(cells[2], cells[5], cells[8] == "1"));
                    break;
                case "record":
                    if (Count(4) && (!identifiers.Add("record:" + cells[1]) ||
                        !uint.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                        !uint.TryParse(cells[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                        Error(Rules.Abi, line, "Duplicate record or invalid layout size; regenerate from A09.");
                    break;
                case "field":
                    if (Count(5) && (!identifiers.Contains("record:" + cells[1]) ||
                        !identifiers.Add("field:" + cells[1] + ":" + cells[2]) ||
                        !uint.TryParse(cells[4], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                        Error(Rules.Abi, line, "Missing record, duplicate field or invalid offset; regenerate from A09.");
                    break;
                case "owner":
                    if (!Count(5)) break;
                    if (!identifiers.Add("owner:" + cells[1]) || cells[2].Length == 0)
                        Error(Rules.Owner, line, "Duplicate owner or missing creating-module destructor; regenerate from A09.");
                    owners.Add((cells[1], cells[2], cells[3], line));
                    break;
                case "callback":
                    if (!Count(6)) break;
                    if (!identifiers.Add("callback:" + cells[1]) ||
                        !uint.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out _) || cells[3].Length == 0)
                        Error(Rules.Async, line, "Callback requires a unique identity, cookie and notification owner.");
                    callbacks.Add((cells[4], cells[5], line));
                    break;
                case "operation":
                    if (!Count(10)) break;
                    if (!identifiers.Add("operation:" + cells[1]) || cells[2].Length == 0 ||
                        cells.Skip(3).Take(6).Distinct(StringComparer.Ordinal).Count() != 6 ||
                        cells.Skip(3).Take(6).Any(symbol => !contract.Exports.ContainsKey(symbol)) ||
                        (cells[9] != "copied-before-accept" && cells[9] != "retirement"))
                        Error(Rules.Async, line, "Operation metadata requires six distinct declared lifecycle exports and the accepted input-retention profile; regenerate from A09.");
                    break;
                default: Error(Rules.Abi, line, "Unknown metadata row; update the compiler tool and maintainer emitter together."); break;
            }
        }
        if (headers.Count != 5 || !Hex(contract.Digest) || !Hex(contract.SourceHash))
            Error(Rules.Abi, text.Lines[0], "Missing required schema/header or invalid SHA-256 identity; regenerate the complete metadata.");
        foreach (var callback in callbacks)
            if (callback.Close == callback.Drain || !contract.Exports.ContainsKey(callback.Close) || !contract.Exports.ContainsKey(callback.Drain))
                Error(Rules.Async, callback.Line, "Callback requires distinct declared close/drain authorities; result completion is not retirement.");
        foreach (var owner in owners)
            if (!contract.Exports.ContainsKey(owner.Release) || (owner.Parent.Length != 0 && !identifiers.Contains("owner:" + owner.Parent)))
                Error(Rules.Owner, owner.Line, "Owner destructor or parent is absent from the same module's declared inventory; use the creating-module release function.");
        foreach (var export in contract.Exports.Values)
            if (export.Lifetimes.Count != export.Parameters.Length)
                Error(Rules.Abi, text.Lines[0], "Every export parameter requires one ordered lifetime row; partial metadata is rejected.");
        if (valid)
        {
            if (Bindings.ContainsKey(contract.Namespace)) Error(Rules.Generator, text.Lines[0], "Two metadata files claim the same generated namespace; use one exact source input.");
            else Bindings.Add(contract.Namespace, contract);
        }

        void Error(DiagnosticDescriptor rule, TextLine line, string reason)
        { valid = false; Diagnostics.Add(Diagnostic.Create(rule, At(path, text, line.Span), reason)); }
    }

    private void ReadApi(string path, SourceText text, CancellationToken cancellation)
    {
        if (HasApiBaseline)
        { Diagnostics.Add(Diagnostic.Create(Rules.Api, At(path, text, text.Lines[0].Span), "Multiple public API baselines are ambiguous; provide one complete reviewed baseline.")); return; }
        HasApiBaseline = true;
        if (text.Lines[0].ToString() != "nativeweave-public-api-v1")
        { Diagnostics.Add(Diagnostic.Create(Rules.Api, At(path, text, text.Lines[0].Span), "Unsupported public API baseline schema.")); return; }
        foreach (var line in text.Lines.Skip(1))
        {
            cancellation.ThrowIfCancellationRequested();
            var value = line.ToString(); if (value.Length == 0) continue;
            var index = value.IndexOf('\t');
            if (index < 2 || index == value.Length - 1 || Api.ContainsKey(value.Substring(0, index)))
            { Diagnostics.Add(Diagnostic.Create(Rules.Api, At(path, text, line.Span), "Malformed or duplicate public API baseline row.")); continue; }
            Api.Add(value.Substring(0, index), value.Substring(index + 1));
        }
    }

    private static bool Hex(string value) => value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    private static Location At(string path, SourceText text, TextSpan span) => Location.Create(path, span, text.Lines.GetLinePositionSpan(span));
}
