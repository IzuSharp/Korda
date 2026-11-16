using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NativeWeave.Generators.Model;

/// <summary>A source location kept outside the semantic model and its digest.</summary>
internal readonly record struct InputLocation(string File, string Path, long Line, long Byte);

/// <summary>A build diagnostic; no input is accepted with a partial model.</summary>
internal sealed class BindingValidationException(string code, InputLocation location, string reason)
    : Exception($"{location.File}:{location.Line}:{location.Byte} {code} {location.Path}: {reason}")
{
    internal string Code { get; } = code;
    internal InputLocation Location { get; } = location;
}

internal sealed record WireField(string Name, string Type, uint Offset, bool Reserved);
internal sealed record WireRecord(string Name, uint Size, uint Alignment, ImmutableArray<WireField> Fields);
internal sealed record BindingParameter(string Name, string Type, string Direction, bool Nullable,
    string BorrowEnd, string Owner, bool Mutable, bool CopyBeforeAccept, uint MaximumBytes);
internal sealed record BindingExport(string Symbol, string ReturnType, string Capability,
    string ErrorProfile, string ThreadProfile, string Reentrancy, ImmutableArray<BindingParameter> Parameters);
internal sealed record BindingOwner(string Kind, string Module, string Release, string CloneExport, string Parent, string ReleaseExecutor, string CloseProgress);
internal sealed record BindingOperation(string Name, string Owner, string Create, string Start,
    string RequestCancel, string TakeResult, string TryRetire, string Release, string InputEnd);
internal sealed record SourceExport(string Symbol, string CallingConvention, string ReturnType,
    bool Generic, ImmutableArray<string> ParameterTypes, InputLocation Location);

/// <summary>Validated, immutable build data. It performs no native loading, generation, I/O or execution.</summary>
internal sealed class BindingDescription
{
    private readonly byte[] canonical;
    internal string Module { get; }
    internal string Digest { get; }
    internal ImmutableArray<WireRecord> Records { get; }
    internal ImmutableArray<BindingExport> Exports { get; }
    internal ImmutableArray<BindingOwner> Owners { get; }
    internal ImmutableArray<BindingOperation> Operations { get; }
    internal ImmutableArray<string> Symbols { get; }
    internal ImmutableArray<string> ManagedSymbols { get; }
    internal byte[] GetCanonicalBytes() => (byte[])canonical.Clone();

    internal BindingDescription(string module, byte[] bytes, ImmutableArray<WireRecord> records,
        ImmutableArray<BindingExport> exports, ImmutableArray<BindingOwner> owners,
        ImmutableArray<BindingOperation> operations, ImmutableArray<string> managedSymbols)
    {
        Module = module; canonical = bytes; Digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Records = records; Exports = exports; Owners = owners; Operations = operations;
        Symbols = exports.Select(x => x.Symbol).Order(StringComparer.Ordinal).ToImmutableArray();
        ManagedSymbols = managedSymbols;
    }

    /// <summary>Requires exact parsed, raw-generated and linked inventories, including unsupported declarations.</summary>
    internal void VerifyInventories(ImmutableArray<SourceExport> parsed,
        IEnumerable<string> generated, IEnumerable<string> linked, CancellationToken cancellationToken = default)
    {
        VerifyExtractionCompleteness(parsed, generated);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(source.Symbol)) Fail("NWABI002", source, "Duplicate native export; choose a unique C symbol.");
            if (source.Generic || source.CallingConvention != "C")
                Fail("NWABI001", source, "Exports must be nongeneric extern C functions.");
            var signature = Exports.FirstOrDefault(x => x.Symbol == source.Symbol);
            if (signature is null) Fail("NWABI002", source, "Export is absent from the semantic sidecar; add its contract.");
            var result = NormalizeRustType(source.ReturnType, source);
            var parameters = source.ParameterTypes.Select(x => NormalizeRustType(x, source)).ToArray();
            if (signature!.ReturnType != result || !signature.Parameters.Select(x => x.Type).SequenceEqual(parameters))
                Fail("NWABI002", source, "Native declaration and sidecar signature differ; regenerate from the same adapter.");
        }
        CheckInventory(seen, "parsed"); CheckInventory(generated, "generated"); CheckInventory(linked, "linked");
    }

    /// <summary>A raw extractor must preserve every parsed export, even a type it cannot lower.</summary>
    internal static void VerifyExtractionCompleteness(IEnumerable<SourceExport> parsed, IEnumerable<string> generated)
    {
        var declarations = parsed.ToArray(); var emitted = generated.ToArray();
        var names = declarations.Select(x => x.Symbol).Order(StringComparer.Ordinal).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count() || emitted.Length != emitted.Distinct(StringComparer.Ordinal).Count() ||
            !names.SequenceEqual(emitted.Order(StringComparer.Ordinal)))
        {
            var omitted = declarations.FirstOrDefault(x => !emitted.Contains(x.Symbol, StringComparer.Ordinal));
            throw new BindingValidationException("NWABI002", omitted?.Location ?? new("generated", "$.exports", 1, 1),
                "Raw extraction omitted, duplicated or added an export; unsupported declarations must fail the build, never disappear.");
        }
    }

    /// <summary>Static/manual descriptors must retain the complete canonical model, not only an engine version.</summary>
    internal void VerifyParity(BindingDescription other)
    {
        if (!canonical.AsSpan().SequenceEqual(other.canonical))
            throw new BindingValidationException("NWGEN001", new("descriptor", "$", 1, 1),
                "Handwritten and static descriptors differ; use the same normalized ABI input.");
    }

    private void CheckInventory(IEnumerable<string> actual, string stage)
    {
        var list = actual.ToArray();
        if (list.Length != list.Distinct(StringComparer.Ordinal).Count() ||
            !Symbols.SequenceEqual(list.Order(StringComparer.Ordinal)))
            throw new BindingValidationException("NWABI002", new(stage, "$.exports", 1, 1),
                "Export inventory differs (missing, duplicate or extra symbol); an extractor success exit is insufficient.");
    }

    private static string NormalizeRustType(string text, SourceExport source)
    {
        var type = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (type is "u8" or "u16" or "u32" or "u64" or "i8" or "i16" or "i32" or "i64" or "f32" or "f64" or "usize" or "()")
            return type == "()" ? "void" : type;
        var prefix = ""; var rest = type; var depth = 0;
        while (rest.StartsWith("*const", StringComparison.Ordinal) || rest.StartsWith("*mut", StringComparison.Ordinal))
        {
            if (++depth > 2) break;
            var readOnly = rest.StartsWith("*const", StringComparison.Ordinal);
            prefix += readOnly ? "const:" : "mut:"; rest = rest[(readOnly ? 6 : 4)..];
        }
        if (rest is "core::ffi::c_void" or "std::ffi::c_void") rest = "void";
        if (depth is 1 or 2 && IsIdentifier(rest)) return prefix + rest;
        Fail("NWABI001", source, "Unsupported Rust ABI shape '" + text + "'; expose fixed-width scalars, named repr(C) records or opaque pointers, never tuples/generics/bool/char/references.");
        return "";
    }

    internal static bool IsIdentifier(string value) => value.Length is > 0 and <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static void Fail(string code, SourceExport source, string message) =>
        throw new BindingValidationException(code, source.Location, message);
}
