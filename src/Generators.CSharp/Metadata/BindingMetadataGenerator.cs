using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace NativeWeave.Generators.CSharp.Metadata
{
    /// <summary>Emits static metadata from the maintainer's validated description without generating imports.</summary>
    /// <remarks>
    /// This compiler-host component performs no file, network, process or native I/O. The .NET
    /// LibraryImport generator consumes the already present C# declarations in the same compilation.
    /// Application generation does not require a Rust toolchain. Invalid metadata produces NWGEN001.
    /// </remarks>
    [Generator(LanguageNames.CSharp)]
    public sealed class BindingMetadataGenerator : IIncrementalGenerator
    {
        /// <summary>Creates the stateless compiler-host generator.</summary>
        public BindingMetadataGenerator() { }

        private static readonly DiagnosticDescriptor InvalidInput = new DiagnosticDescriptor("NWGEN001",
            "Invalid NativeWeave metadata", "{0}", "NativeWeave.Binding", DiagnosticSeverity.Error, true,
            description: "Regenerate the bounded metadata with the A10 maintainer command. Raw LibraryImport generation never occurs in a peer compiler generator.");

        /// <summary>Registers value-comparable AdditionalText transforms and deterministic static source output.</summary>
        /// <param name="context">The compiler-supplied incremental registration context.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var inputs = context.AdditionalTextsProvider
                .Where(file => file.Path.EndsWith(".nwmetadata", StringComparison.OrdinalIgnoreCase))
                .Select((file, cancellation) => Parse(file.GetText(cancellation)?.ToString(), cancellation))
                .WithComparer(SemanticInput.Comparer)
                .WithTrackingName("NativeWeaveMetadata");
            context.RegisterSourceOutput(inputs.Collect(), (production, values) =>
            {
                var modules = new HashSet<string>(StringComparer.Ordinal);
                foreach (var input in values)
                {
                    production.CancellationToken.ThrowIfCancellationRequested();
                    if (input.Error.Length != 0 || !modules.Add(input.Identity))
                    {
                        production.ReportDiagnostic(Diagnostic.Create(InvalidInput, Location.None,
                            input.Error.Length != 0 ? input.Error : "Duplicate module metadata; include one canonical payload per module."));
                        return;
                    }
                }
                foreach (var input in values.OrderBy(x => x.Identity, StringComparer.Ordinal))
                    production.AddSource(input.Identity + ".Metadata.g.cs", SourceText.From(input.Source, Encoding.UTF8));
            });
        }

        internal static SemanticInput Parse(string? text, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (text == null || text.Length > 1024 * 1024) return SemanticInput.Invalid("Metadata is missing or exceeds 1 MiB.");
            var lines = text.Split('\n');
            if (lines.Length > 32768 || lines[0] != "nativeweave-metadata-v1" || text.IndexOf('\r') >= 0)
                return SemanticInput.Invalid("Unknown metadata format; use canonical UTF-8 LF output.");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var exports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1))
            {
                cancellation.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;
                var pieces = line.Split('\t');
                switch (pieces[0])
                {
                    case "module":
                    case "digest":
                    case "namespace":
                    case "library":
                    case "source_sha256":
                        if (pieces.Length != 2 || fields.ContainsKey(pieces[0])) return SemanticInput.Invalid("Duplicate or malformed metadata header.");
                        fields.Add(pieces[0], pieces[1]); break;
                    case "export":
                        if (pieces.Length != 4 || !exports.Add(pieces[1])) return SemanticInput.Invalid("Duplicate or malformed export metadata.");
                        break;
                    case "record": if (pieces.Length != 4) return SemanticInput.Invalid("Malformed record metadata."); break;
                    case "field": if (pieces.Length != 5) return SemanticInput.Invalid("Malformed field metadata."); break;
                    case "owner": if (pieces.Length != 5) return SemanticInput.Invalid("Malformed owner metadata."); break;
                    case "parameter": if (pieces.Length != 10) return SemanticInput.Invalid("Malformed parameter metadata."); break;
                    case "callback": if (pieces.Length != 6) return SemanticInput.Invalid("Malformed callback metadata."); break;
                    case "operation": if (pieces.Length != 10) return SemanticInput.Invalid("Malformed operation metadata."); break;
                    default: return SemanticInput.Invalid("Unknown required metadata row; regenerate with the supported schema.");
                }
            }
            if (fields.Count != 5 || !fields.TryGetValue("module", out var module) || !fields.TryGetValue("digest", out var digest) ||
                !fields.TryGetValue("source_sha256", out var sourceDigest) || !fields.TryGetValue("namespace", out var ns) ||
                !fields.TryGetValue("library", out var library) || !Digest(digest) || !Digest(sourceDigest) ||
                module.Length == 0 || module.Length > 128 || !module.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') ||
                library.Length == 0 || !library.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') ||
                ns.Length > 512 || ns.Split('.').Any(part => !Identifier(part)))
                return SemanticInput.Invalid("Metadata header is incomplete or contains an invalid identity/hash.");
            string identity;
            using (var hash = SHA256.Create()) identity = "M_" + string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(module)).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            var source = "// <auto-generated />\n#nullable enable\nnamespace " + string.Join(".", ns.Split('.').Select(p => "@" + p)) + "\n{\n" +
                "    /// <summary>Compile-time identity of validated native bindings; no runtime scan.</summary>\n" +
                "    internal static class BindingMetadata_" + identity + "\n    {\n" +
                "        internal const string SchemaSha256 = \"" + digest + "\";\n" +
                "        internal const string Module = \"" + module + "\";\n" +
                "        internal const int ExportCount = " + exports.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";\n" +
                "    }\n}\n";
            return new SemanticInput(identity, source, "");
        }

        private static bool Digest(string text) => text.Length == 64 && text.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        private static bool Identifier(string text) => text.Length > 0 && (text[0] == '_' || char.IsLetter(text[0])) && text.All(c => c == '_' || char.IsLetterOrDigit(c));
    }

    internal sealed class SemanticInput
    {
        internal readonly string Identity;
        internal readonly string Source;
        internal readonly string Error;
        internal SemanticInput(string identity, string source, string error) { Identity = identity; Source = source; Error = error; }
        internal static SemanticInput Invalid(string error) => new SemanticInput("", "", error);
        internal static readonly IEqualityComparer<SemanticInput> Comparer = new Equality();
        private sealed class Equality : IEqualityComparer<SemanticInput>
        {
            public bool Equals(SemanticInput? x, SemanticInput? y) => ReferenceEquals(x, y) || x != null && y != null &&
                x.Identity == y.Identity && x.Source == y.Source && x.Error == y.Error;
            public int GetHashCode(SemanticInput value) => value.Identity.GetHashCode() ^ value.Source.GetHashCode() ^ value.Error.GetHashCode();
        }
    }
}
