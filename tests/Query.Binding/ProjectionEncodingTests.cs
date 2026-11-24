using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;
using Korda.Query.Internal;

internal static class ProjectionEncodingTests
{
    // The mixed-shape bytes are fixed outputs of the unchanged before Native AOT
    // encoder, source 909f942180573f5d4d6dbccfcf4a7180023259e3363098747907392165d2ccda.
    // They are not emitted again by the candidate during the test.
    private static readonly byte[] MetadataGolden = Convert.FromHexString(
        "01000d00fc010000020000000000000001000500ac01000030000000000000000200050020000000dc010000000000000400000063000000" +
        "0100160063000000040000000000000001000100040000005000000000000000020004000700000054000000000000000300010004000000" +
        "5b0000000000000004000100040000005f00000000000000000000006669656c645f30030000000000000069000000010016006900000004" +
        "0000000000000001000100040000005000000000000000020004000d00000054000000000000000300010004000000610000000000000004" +
        "00010004000000650000000000000001000000e8aea2e58d955f315ff09fa680050000000100000063000000010016006300000004000000" +
        "00000000010001000400000050000000000000000200040007000000540000000000000003000100040000005b0000000000000004000100" +
        "040000005f00000000000000020000006669656c645f320d0000000000000069000000010016006900000004000000000000000100010004" +
        "0000005000000000000000020004000d00000054000000000000000300010004000000610000000000000004000100040000006500000000" +
        "00000003000000e8aea2e58d955f335ff09fa6800400000000000000b609f61ff12dc427743bb947bd58a4c393dd815b17ca192059c00d9a" +
        "9b69d74d");

    private static readonly byte[] ExpressionsGolden = Convert.FromHexString(
        "01000d005c0200000200000000000000010005000c020000300000000000000002000500200000003c02000000000000040000007b000000" +
        "010016007b000000050000000000000001000100040000006000000000000000020004000700000064000000000000000300010004000000" +
        "6b0000000000000004000100040000006f0000000000000005000200080000007300000000000000000000006669656c645f300300000000" +
        "0000000100000001000000810000000100160081000000050000000000000001000100040000006000000000000000020004000d00000064" +
        "0000000000000003000100040000007100000000000000040001000400000075000000000000000500020008000000790000000000000001" +
        "000000e8aea2e58d955f315ff09fa680050000000100000002000000010000007b000000010016007b000000050000000000000001000100" +
        "0400000060000000000000000200040007000000640000000000000003000100040000006b0000000000000004000100040000006f000000" +
        "0000000005000200080000007300000000000000020000006669656c645f320d000000000000000300000001000000810000000100160081" +
        "000000050000000000000001000100040000006000000000000000020004000d000000640000000000000003000100040000007100000000" +
        "000000040001000400000075000000000000000500020008000000790000000000000003000000e8aea2e58d955f335ff09fa68004000000" +
        "000000000400000001000000b609f61ff12dc427743bb947bd58a4c393dd815b17ca192059c00d9a9b69d74d");

    private static readonly byte[] EightExpressionsGolden = Convert.FromHexString(
        "01000d006404000002000000000000000100050014040000300000000000000002000500200000004404000000000000080000007b000000" +
        "010016007b000000050000000000000001000100040000006000000000000000020004000700000064000000000000000300010004000000" +
        "6b0000000000000004000100040000006f0000000000000005000200080000007300000000000000000000006669656c645f300300000000" +
        "0000000100000001000000810000000100160081000000050000000000000001000100040000006000000000000000020004000d00000064" +
        "0000000000000003000100040000007100000000000000040001000400000075000000000000000500020008000000790000000000000001" +
        "000000e8aea2e58d955f315ff09fa680050000000100000002000000010000007b000000010016007b000000050000000000000001000100" +
        "0400000060000000000000000200040007000000640000000000000003000100040000006b0000000000000004000100040000006f000000" +
        "0000000005000200080000007300000000000000020000006669656c645f320d000000000000000300000001000000810000000100160081" +
        "000000050000000000000001000100040000006000000000000000020004000d000000640000000000000003000100040000007100000000" +
        "000000040001000400000075000000000000000500020008000000790000000000000003000000e8aea2e58d955f335ff09fa68004000000" +
        "0000000004000000010000007b000000010016007b0000000500000000000000010001000400000060000000000000000200040007000000" +
        "640000000000000003000100040000006b0000000000000004000100040000006f0000000000000005000200080000007300000000000000" +
        "040000006669656c645f34030000000000000005000000010000008100000001001600810000000500000000000000010001000400000060" +
        "00000000000000020004000d0000006400000000000000030001000400000071000000000000000400010004000000750000000000000005" +
        "00020008000000790000000000000005000000e8aea2e58d955f355ff09fa680050000000100000006000000010000007b00000001001600" +
        "7b0000000500000000000000010001000400000060000000000000000200040007000000640000000000000003000100040000006b000000" +
        "0000000004000100040000006f0000000000000005000200080000007300000000000000060000006669656c645f360d0000000000000007" +
        "00000001000000810000000100160081000000050000000000000001000100040000006000000000000000020004000d0000006400000000" +
        "00000003000100040000007100000000000000040001000400000075000000000000000500020008000000790000000000000007000000e8" +
        "aea2e58d955f375ff09fa6800400000000000000080000000100000054fec8cf91da407262f523fa71a8b7086aa157d440db1abb54f7bb50" +
        "85e8d208");

    private static readonly byte[] ConfiguredGolden = Convert.FromHexString(
        "01000d00fc010000020000000000000001000500ac01000030000000000000000200050020000000dc010000000000000400000063000000" +
        "0100160063000000040000000000000001000100040000005000000000000000020004000700000054000000000000000300010004000000" +
        "5b0000000000000004000100040000005f00000000000000000000006669656c645f30030000000000000069000000010016006900000004" +
        "0000000000000001000100040000005000000000000000020004000d00000054000000000000000300010004000000610000000000000004" +
        "00010004000000650000000000000001000000e8aea2e58d955f315ff09fa680050000000100000063000000010016006300000004000000" +
        "00000000010001000400000050000000000000000200040007000000540000000000000003000100040000005b0000000000000004000100" +
        "040000005f00000000000000020000006669656c645f320d0000800000000069000000010016006900000004000000000000000100010004" +
        "0000005000000000000000020004000d00000054000000000000000300010004000000610000000000000004000100040000006500000000" +
        "00000003000000e8aea2e58d955f335ff09fa6800400000000000000b609f61ff12dc427743bb947bd58a4c393dd815b17ca192059c00d9a" +
        "9b69d74d");

    private static readonly byte[] ScalarGolden = Convert.FromHexString(
        "01000d00b400000002000000000000000100050064000000300000000000000002000500200000009400000000000000010000005c000000" +
        "010016005c000000040000000000000001000100040000005000000000000000020004000000000054000000000000000300010004000000" +
        "540000000000000004000100040000005800000000000000000000000300000000000000277f5cbab37077a33b213a45f83b88986863070e" +
        "96c0a65d26322248373f3973");

    private static readonly byte[] ScalarExpressionsGolden = Convert.FromHexString(
        "01000d00cc0000000200000000000000010005007c00000030000000000000000200050020000000ac000000000000000100000074000000" +
        "0100160074000000050000000000000001000100040000006000000000000000020004000000000064000000000000000300010004000000" +
        "64000000000000000400010004000000680000000000000005000200080000006c00000000000000000000000300000000000000ffffffff" +
        "ffffffff277f5cbab37077a33b213a45f83b88986863070e96c0a65d26322248373f3973");

    private const string ExactMetadataLimitSha256 = "f34c7fd3475814b75638f475a9583244fccca75188cb12db0b886c0a9d1e8651";

    private const string ExactExpressionLimitSha256 = "9163aacd11f10612f027f3931c1e460df6255f6020fc6e770d42644c45408527";
    private static readonly Projection<long> SharedProjection = CreateProjection(4);
    private static readonly ulong[] SharedExpressions = Expressions(4);
    private static readonly Func<CodecDescriptor, uint> Bind = static descriptor => descriptor.Id;

    internal static void Run(string output)
    {
        var assertions = 0;
        void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException(message); }
        void Reject<T>(Action action, string message) where T : Exception
        {
            assertions++;
            try { action(); }
            catch (Exception error)
            {
                if (error.GetType() == typeof(T) && error.Message == message) return;
                throw new InvalidOperationException("Projection exception type/message changed.", error);
            }
            throw new InvalidOperationException("Projection unexpectedly accepted invalid input.");
        }

        var fields = CreateFields(4);
        var savedFields = fields.ToArray();
        var projection = Projection<long>.Create(fields, static _ => throw new InvalidOperationException("Encoding invoked materialization."));
        var expressions = Expressions(4);
        var savedExpressions = expressions.ToArray();
        var canonical = projection.CanonicalBytes.ToArray();
        var shape = projection.ShapeHash;
        // The public projection must own its sequence, independently of later caller edits.
        Array.Reverse(fields);
        Check(projection.Fields.AsSpan().SequenceEqual(savedFields), "Projection retained a mutable caller sequence.");
        foreach (var withExpressions in new[] { false, true })
        {
            var order = new List<uint>();
            var bytes = OperandEncoding.Projection(projection, descriptor =>
            { order.Add(descriptor.Id); return descriptor.Id; }, withExpressions ? expressions : []);
            Check(bytes.AsSpan().SequenceEqual(withExpressions ? ExpressionsGolden : MetadataGolden), "Fixed baseline projection wire changed.");
            Check(order.SequenceEqual(new uint[] { 3, 5, 13, 4 }), "Codec binding count/order changed.");
            Check(bytes.AsSpan(bytes.Length - 32).SequenceEqual(Convert.FromHexString(shape)), "Logical shape digest changed.");
            Check(projection.CanonicalBytes.AsSpan().SequenceEqual(canonical) && projection.ShapeHash == shape,
                "Encoding mutated projection canonical metadata.");
            Check(projection.Fields.AsSpan().SequenceEqual(savedFields) && expressions.AsSpan().SequenceEqual(savedExpressions),
                "Encoding mutated field descriptors or expression values.");
        }
        var configuredOrder = new List<CodecDescriptor>();
        var configured = OperandEncoding.Projection(projection, descriptor =>
        {
            configuredOrder.Add(descriptor);
            return descriptor.Configuration is null ? descriptor.Id : 0x8000000du;
        });
        Check(configured.AsSpan().SequenceEqual(ConfiguredGolden), "Source-bound configured codec ID was replaced by its logical ID.");
        Check(ReferenceEquals(configuredOrder[2], projection.Fields[2].Descriptor)
            && configuredOrder[2].Configuration == "{\"precision\":18,\"scale\":2}", "Configured codec descriptor changed before binding.");
        Check(projection.ShapeHash == shape, "Source-specific codec binding changed the logical shape.");

        var scalar = Projection<long>.Scalar(Codecs.Int64);
        Check(OperandEncoding.Projection(scalar, Bind).AsSpan().SequenceEqual(ScalarGolden), "Empty-alias scalar metadata wire changed.");
        Check(OperandEncoding.Projection(scalar, Bind, [ulong.MaxValue]).AsSpan().SequenceEqual(ScalarExpressionsGolden),
            "Scalar expression width, order or empty alias changed.");

        var nullMessage = new NullReferenceException().Message;
        Reject<NullReferenceException>(() => OperandEncoding.Projection<long>(null!, Bind), nullMessage);
        Reject<NullReferenceException>(() => OperandEncoding.Projection(projection, null!), nullMessage);
        var calls = new List<uint>();
        uint CountBind(CodecDescriptor descriptor) { calls.Add(descriptor.Id); return descriptor.Id; }
        Reject<ArgumentException>(() => OperandEncoding.Projection(projection, CountBind, new ulong[1]),
            new ArgumentException("Projection expression count differs.", "expressions").Message);
        Check(calls.Count == 0, "Expression length validation bound a codec.");
        Reject<ApplicationException>(() => OperandEncoding.Projection(projection, descriptor =>
        {
            calls.Add(descriptor.Id);
            if (calls.Count == 3) throw new ApplicationException("fixture bind failure");
            return descriptor.Id;
        }), "fixture bind failure");
        Check(calls.SequenceEqual(new uint[] { 3, 5, 13 }), "Binder failure visited later fields or reordered earlier fields.");

        // Deliberately invalid internal metadata tests the compatibility fallback;
        // public ResultField.Create and Projection.Create cannot produce this state.
        var malformed = CreateProjection(4);
        malformed.Fields[1] = new ResultField("\ud800", Codecs.String.Descriptor);
        var strictUtf8 = new UTF8Encoding(false, true);
        string invalidUnicodeMessage;
        try { strictUtf8.GetBytes("\ud800"); throw new InvalidOperationException("Invalid Unicode accepted."); }
        catch (EncoderFallbackException error) { invalidUnicodeMessage = error.Message; }
        calls.Clear();
        Reject<EncoderFallbackException>(() => OperandEncoding.Projection(malformed, CountBind), invalidUnicodeMessage);
        Check(calls.SequenceEqual(new uint[] { 3 }), "Invalid later UTF-8 changed earlier binding side effects.");
        calls.Clear();
        Reject<ApplicationException>(() => OperandEncoding.Projection(malformed, descriptor =>
        { calls.Add(descriptor.Id); throw new ApplicationException("first binding wins"); }), "first binding wins");
        Check(calls.SequenceEqual(new uint[] { 3 }), "UTF-8 preflight changed binder failure precedence.");

        const int maximum = DbWire.MaxEncodedBytes;
        const string sizeError = "Control record exceeds limit.";
        foreach (var withExpressions in new[] { false, true })
        {
            var overhead = withExpressions ? 204 : 180;
            var atLimit = Projection<long>.Create([ResultField.Create(new string('x', maximum - overhead), Codecs.Int64)], static _ => 0L);
            calls.Clear();
            var boundary = OperandEncoding.Projection(atLimit, CountBind, withExpressions ? new ulong[] { ulong.MaxValue } : []);
            Check(boundary.Length == maximum && BinaryPrimitives.ReadUInt32LittleEndian(boundary.AsSpan(4)) == maximum,
                "Projection exact 4 MiB boundary changed.");
            Check(Convert.ToHexStringLower(SHA256.HashData(boundary)) == (withExpressions ? ExactExpressionLimitSha256 : ExactMetadataLimitSha256),
                "Projection complete wire at the 4 MiB boundary changed.");
            Check(calls.SequenceEqual(new uint[] { 3 }), "Exact boundary did not bind once.");
            var over = Projection<long>.Create([ResultField.Create(new string('x', maximum - overhead + 1), Codecs.Int64)], static _ => 0L);
            calls.Clear();
            Reject<ArgumentException>(() => OperandEncoding.Projection(over, CountBind, withExpressions ? new ulong[] { ulong.MaxValue } : []), sizeError);
            Check(calls.SequenceEqual(new uint[] { 3 }), "Outer one-byte overflow changed binding side effects.");
        }

        var innerOverflow = Projection<long>.Create([
            ResultField.Create(new string('x', maximum - 91), Codecs.Int64), ResultField.Create("later", Codecs.Boolean)
        ], static _ => 0L);
        calls.Clear();
        Reject<ArgumentException>(() => OperandEncoding.Projection(innerOverflow, CountBind), sizeError);
        Check(calls.SequenceEqual(new uint[] { 3 }), "Oversized first field bound a later field.");
        calls.Clear();
        Reject<ApplicationException>(() => OperandEncoding.Projection(innerOverflow, descriptor =>
        { calls.Add(descriptor.Id); throw new ApplicationException("binding before inner limit"); }), "binding before inner limit");
        Check(calls.SequenceEqual(new uint[] { 3 }), "Inner size validation moved before its own binding.");

        var outerOverflow = Projection<long>.Create([
            ResultField.Create(new string('x', maximum / 2), Codecs.Int64),
            ResultField.Create(new string('y', maximum / 2), Codecs.Boolean),
            ResultField.Create("last", Codecs.String)
        ], static _ => 0L);
        calls.Clear();
        Reject<ArgumentException>(() => OperandEncoding.Projection(outerOverflow, CountBind), sizeError);
        Check(calls.SequenceEqual(new uint[] { 3, 4, 5 }), "Outer cumulative overflow stopped binding early.");
        calls.Clear();
        Reject<ApplicationException>(() => OperandEncoding.Projection(outerOverflow, descriptor =>
        {
            calls.Add(descriptor.Id);
            if (calls.Count == 3) throw new ApplicationException("binding before outer limit");
            return descriptor.Id;
        }), "binding before outer limit");
        Check(calls.SequenceEqual(new uint[] { 3, 4, 5 }), "Outer size validation changed the original binder failure order.");

        var outputs = new byte[32][];
        Parallel.For(0, outputs.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            var visited = new List<uint>();
            outputs[index] = OperandEncoding.Projection(SharedProjection, descriptor =>
            { visited.Add(descriptor.Id); return descriptor.Id; }, SharedExpressions);
            if (!visited.SequenceEqual(new uint[] { 3, 5, 13, 4 })) throw new InvalidOperationException("Parallel binding order changed.");
        });
        for (var i = 0; i < outputs.Length; i++)
        {
            Check(outputs[i].AsSpan().SequenceEqual(ExpressionsGolden), "Parallel shared projection changed bytes.");
            for (var j = 0; j < i; j++) Check(!ReferenceEquals(outputs[i], outputs[j]), "Concurrent callers shared an output array.");
        }
        Array.Fill(outputs[0], (byte)0xff);
        Check(outputs[1].AsSpan().SequenceEqual(ExpressionsGolden)
            && OperandEncoding.Projection(SharedProjection, Bind, SharedExpressions).AsSpan().SequenceEqual(ExpressionsGolden),
            "Caller-owned output mutation affected another encoding.");
        Check(SharedExpressions.AsSpan().SequenceEqual(Expressions(4))
            && SharedProjection.CanonicalBytes.AsSpan().SequenceEqual(canonical), "Parallel encoding mutated shared input.");
        assertions += RunScratchContracts(projection, malformed, innerOverflow, outerOverflow);
        File.WriteAllText(Path.Combine(output, "projection-encoding-assertions.txt"), assertions.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("PASS projection encoding goldens, binding/error order, 4 MiB boundaries and concurrent output ownership.");
    }

    private static bool IsFilled(ReadOnlySpan<byte> bytes, byte expected)
    {
        foreach (var value in bytes) if (value != expected) return false;
        return true;
    }
    private static byte[] ScratchCopy(Projection<long> projection, Func<CodecDescriptor, uint> binder, ulong[] expressions)
    {
        Span<byte> scratch = stackalloc byte[OperandEncoding.StackScratchBytes];
        scratch.Fill(0xa5);
        return OperandEncoding.ProjectionInto(projection, binder, scratch, expressions).ToArray();
    }
    private static int RunScratchContracts(Projection<long> projection, Projection<long> malformed,
        Projection<long> innerOverflow, Projection<long> outerOverflow)
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        Check(OperandEncoding.StackScratchBytes == 4096, "Scratch cutoff changed without recalibration.");
        Span<byte> guarded = stackalloc byte[4096 + 32];
        var scratch = guarded.Slice(16, 4096);
        foreach (var hasExpressions in new[] { false, true })
        {
            var tokens = hasExpressions ? Expressions(4) : Array.Empty<ulong>();
            guarded.Fill(0xa5);
            var encoded = OperandEncoding.ProjectionInto(projection, Bind, scratch, tokens);
            Check(encoded.SequenceEqual(hasExpressions ? ExpressionsGolden : MetadataGolden), "Poisoned scratch changed complete projection wire or reserved bytes.");
            Check(IsFilled(guarded[..16], 0xa5) && IsFilled(guarded[(16 + encoded.Length)..], 0xa5), "Projection writer escaped its exact slice.");
            var copy = encoded.ToArray();
            scratch.Fill(0xcc);
            Check(encoded[0] == 0xcc && copy[0] == 1, "Small projection did not borrow caller scratch, or owned copy aliases it.");
            foreach (var extra in new[] { 0, 1 })
            {
                var name = new string('x', 4096 - (hasExpressions ? 204 : 180) + extra);
                var boundary = Projection<long>.Create([ResultField.Create(name, Codecs.Int64)], static _ => 0L);
                var boundaryTokens = hasExpressions ? new ulong[] { ulong.MaxValue } : Array.Empty<ulong>();
                var expected = OperandEncoding.Projection(boundary, Bind, boundaryTokens);
                Check(expected.Length == 4096 + extra, "Scratch boundary fixture size changed.");
                guarded.Fill(0xa5);
                var result = OperandEncoding.ProjectionInto(boundary, Bind, scratch, boundaryTokens);
                Check(result.SequenceEqual(expected), "4096/4097 scratch boundary wire differs from owned encoding.");
                Check(IsFilled(guarded[..16], 0xa5) && IsFilled(guarded[(16 + (extra == 0 ? 4096 : 0))..], 0xa5), "Scratch boundary guard or fallback storage was modified.");
                scratch.Fill(0xcc);
                Check(extra == 0 ? result[0] == 0xcc : result.SequenceEqual(expected), "4096 must borrow scratch; 4097 must survive scratch overwrite through heap fallback.");
            }
        }
        void SameFailure(Projection<long> item, ulong[] tokens, int throwAt = 0, bool nullBinder = false)
        {
            Exception? previous = null;
            uint[] previousOrder = [];
            foreach (var useScratch in new[] { false, true })
            {
                var order = new List<uint>();
                Func<CodecDescriptor, uint> binder = descriptor =>
                {
                    order.Add(descriptor.Id);
                    if (order.Count == throwAt) throw new ApplicationException("scratch binding failure");
                    return descriptor.Id;
                };
                if (nullBinder) binder = null!;
                Exception? observed = null;
                try
                {
                    if (useScratch) _ = ScratchCopy(item, binder, tokens);
                    else _ = OperandEncoding.Projection(item, binder, tokens);
                }
                catch (Exception error) { observed = error; }
                Check(observed is not null, "Scratch failure fixture unexpectedly succeeded.");
                if (previous is not null)
                    Check(observed!.GetType() == previous.GetType() && observed.Message == previous.Message
                        && order.SequenceEqual(previousOrder), "Into changed exception type/message or binding side-effect order.");
                previous = observed; previousOrder = order.ToArray();
            }
        }
        SameFailure(null!, []);
        SameFailure(projection, [], nullBinder: true);
        SameFailure(projection, [1]);
        SameFailure(projection, [], throwAt: 3);
        SameFailure(malformed, []);
        SameFailure(malformed, [], throwAt: 1);
        SameFailure(innerOverflow, []);
        SameFailure(outerOverflow, []);
        SameFailure(outerOverflow, [], throwAt: 3);
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            Span<byte> local = stackalloc byte[OperandEncoding.StackScratchBytes];
            local.Fill((byte)(0x80 + index));
            var view = OperandEncoding.ProjectionInto(SharedProjection, Bind, local, SharedExpressions);
            if (!view.SequenceEqual(ExpressionsGolden)) throw new InvalidOperationException("Concurrent poisoned scratch changed wire.");
            var owned = view.ToArray();
            local.Fill(0xa5);
            if (!owned.AsSpan().SequenceEqual(ExpressionsGolden) || view[0] != 0xa5)
                throw new InvalidOperationException("Concurrent call failed its local scratch/owned-copy boundary.");
        });
        checks++;
        return checks;
    }

    private static byte[] OwnedSelection<T>(QueryBinding query, Projection<T> projection, ExpressionReference[] expressions)
    {
        var tokens = OperandEncoding.Tokens(query, expressions);
        var handles = expressions.Select(expression => expression.In(query)).ToArray();
        return OperandEncoding.Clause(OperandEncoding.Bytes(1, tokens, 8),
            OperandEncoding.Bytes(2, OperandEncoding.Projection(projection, Bind, handles)));
    }
    internal static int RunSelection<T>(QueryBinding query, Projection<T> projection,
        ExpressionReference[] expressions, ExpressionReference foreign)
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var original = expressions.ToArray();
        var expected = OwnedSelection(query, projection, expressions);
        Span<byte> guarded = stackalloc byte[4096 + 32];
        var scratch = guarded.Slice(16, 4096);
        var calls = 0;
        uint CountBind(CodecDescriptor descriptor) { calls++; return descriptor.Id; }
        guarded.Fill(0xa5);
        Check(OperandEncoding.TrySelectionInto(query, projection, CountBind, expressions, scratch, out var length), "Small selection did not use scratch.");
        Check(scratch[..length].SequenceEqual(expected) && calls == expressions.Length,
            "Poisoned SELECT wire/reserved bytes or binder count differs from owned encoding.");
        Check(IsFilled(guarded[..16], 0xa5) && IsFilled(guarded[(16 + length)..], 0xa5), "SELECT writer escaped its slice.");
        guarded.Fill(0xa5); calls = 0;
        Check(!OperandEncoding.TrySelectionInto(query, projection, CountBind, expressions, scratch[..1], out length)
            && length == 0 && calls == 0 && IsFilled(guarded, 0xa5), "Insufficient scratch wrote bytes or bound codecs before fallback.");
        var wide = Projection<long>.Create(Enumerable.Range(0, 33).Select(i => ResultField.Create("wide_" + i.ToString(CultureInfo.InvariantCulture), Codecs.Int64)).ToArray(), static _ => 0L);
        var repeated = Enumerable.Repeat(expressions[0], 33).ToArray();
        guarded.Fill(0xa5); calls = 0;
        Check(!OperandEncoding.TrySelectionInto(query, wide, CountBind, repeated, scratch, out length)
            && length == 0 && calls == 0 && IsFilled(guarded, 0xa5), "More than 32 fields did not preserve untouched fallback.");
        foreach (var extra in new[] { 0, 1 })
        {
            var boundary = Projection<long>.Create([ResultField.Create(new string('x', 4096 - 260 + extra), Codecs.Int64)], static _ => 0L);
            ExpressionReference[] one = [expressions[0]];
            var boundaryExpected = OwnedSelection(query, boundary, one);
            Check(boundaryExpected.Length == 4096 + extra, "SELECT boundary fixture length changed.");
            guarded.Fill(0xa5); calls = 0;
            var fit = OperandEncoding.TrySelectionInto(query, boundary, CountBind, one, scratch, out length);
            if (extra == 0)
                Check(fit && length == 4096 && calls == 1 && scratch.SequenceEqual(boundaryExpected), "Exact 4096-byte SELECT should borrow and fill scratch.");
            else
                Check(!fit && length == 0 && calls == 0 && IsFilled(guarded, 0xa5), "4097-byte SELECT changed fallback side effects.");
            Check(IsFilled(guarded[..16], 0xa5) && IsFilled(guarded[4112..], 0xa5), "SELECT boundary guard changed.");
        }
        foreach (var invalid in new ExpressionReference[] { default, foreign })
        {
            var bad = expressions.ToArray(); bad[^1] = invalid;
            guarded.Fill(0xa5); calls = 0;
            Exception? observed = null;
            try { OperandEncoding.TrySelectionInto(query, projection, CountBind, bad, scratch, out _); }
            catch (Exception error) { observed = error; }
            Check(observed is ArgumentException argument && argument.GetType() == typeof(ArgumentException)
                && argument.Message == "Default or foreign expression reference." && calls == 0,
                "All tokens must be validated before any codec binding.");
        }
        guarded.Fill(0xa5); calls = 0;
        Exception? bindFailure = null;
        try
        {
            OperandEncoding.TrySelectionInto(query, projection, descriptor =>
            { calls++; throw new ApplicationException("selection binding failure"); }, expressions, scratch, out _);
        }
        catch (Exception error) { bindFailure = error; }
        Check(bindFailure is ApplicationException application && application.Message == "selection binding failure" && calls == 1,
            "Selection binder failure changed or retried a bind.");
        Check(expressions.AsSpan().SequenceEqual(original), "SELECT changed caller expression references.");
        guarded.Fill(0xa5); calls = 0;
        Check(OperandEncoding.TrySelectionInto(query, projection, CountBind, expressions, scratch, out length), "Final borrowed selection failed.");
        query.Clause(6, scratch[..length]);
        scratch.Fill(0xa5); // Native must already own its persistent fields before this point.
        return checks;
    }

    internal static void RunAllocation(string output)
    {
        if (RuntimeFeature.IsDynamicCodeSupported || !OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Projection allocation measurement requires Linux x64 Native AOT.");
        const int warmup = 2048, operations = 8192, repeats = 3;
        const long measuredMaximumWindowBytes = 9_437_944, measuredWindowRangeBytes = 760;
        const long maximumWindowBytes = measuredMaximumWindowBytes + measuredWindowRangeBytes;
        var projection = CreateProjection(8);
        var expressions = Expressions(8);
        var samples = new ProjectionAllocationSample[repeats];
        var checksumPerOperation = Consume(EightExpressionsGolden, EightExpressionsGolden);
        long warmChecksum = 0;
        for (var i = 0; i < warmup; i++) warmChecksum += Consume(OperandEncoding.Projection(projection, Bind, expressions), EightExpressionsGolden);
        if (warmChecksum != checksumPerOperation * warmup) throw new InvalidOperationException("Projection allocation warmup changed.");
        _ = Stopwatch.GetTimestamp(); _ = GC.GetTotalAllocatedBytes(true); _ = GC.GetAllocatedBytesForCurrentThread();
        _ = GC.CollectionCount(0); _ = GC.CollectionCount(1); _ = GC.CollectionCount(2);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            var gen0Before = GC.CollectionCount(0); var gen1Before = GC.CollectionCount(1); var gen2Before = GC.CollectionCount(2);
            var started = Stopwatch.GetTimestamp(); var before = GC.GetTotalAllocatedBytes(true);
            var threadBefore = GC.GetAllocatedBytesForCurrentThread();
            long checksum = 0;
            for (var i = 0; i < operations; i++) checksum += Consume(OperandEncoding.Projection(projection, Bind, expressions), EightExpressionsGolden);
            var threadAfter = GC.GetAllocatedBytesForCurrentThread(); var after = GC.GetTotalAllocatedBytes(true); var ended = Stopwatch.GetTimestamp();
            var gen0After = GC.CollectionCount(0); var gen1After = GC.CollectionCount(1); var gen2After = GC.CollectionCount(2);
            samples[repeat] = new(repeat, operations, before, after, after - before, threadBefore, threadAfter,
                gen0Before, gen0After, gen1Before, gen1After, gen2Before, gen2After, checksum, ended - started);
        }
        var receipt = new ProjectionAllocationReceipt("DB-Perf-01", "internal-projection-encoding-short-regression", true,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessId,
            warmup, EightExpressionsGolden.Length, 12184,
            "e5ebda3b63249799d1507487f07b47e6afbc12688125c459bd0c0d031c85753f",
            measuredMaximumWindowBytes, measuredWindowRangeBytes, maximumWindowBytes,
            "d26bb04627963c515d5709f3a223b510d91f7928ff24014e6506efd833ded718",
            "Raw whole-process cumulative allocated bytes and GC endpoints; current-thread counters are a separate cross-check. Prebuilt fields, expressions, binder, warmup and reporting are outside. Every operation encodes and fully compares/consumes all wire bytes. No subtraction, SQL, public PackageReference or concurrency-coverage claim.",
            "The owned output byte[] remains framework allocation. This fixture produces no business DTO/list/string result. GC counts describe the shared process heap and cannot be split by framework/business. Whole Encode is not 0 B.",
            "Budget per 8192-operation window is the maximum raw whole-process count across three independent Native AOT processes, each with three windows, plus one observed window range: 9437944 + 760 = 9438704 B. The first-window 760 B process/thread difference is not attributed or subtracted; all raw counts and GC endpoints remain intact.",
            0, false, Stopwatch.Frequency, samples);
        File.WriteAllText(Path.Combine(output, "projection-encoding-allocation.json"),
            JsonSerializer.Serialize(receipt, ProjectionTestJson.Default.ProjectionAllocationReceipt));
        foreach (var sample in samples)
        {
            if (sample.Checksum != checksumPerOperation * operations) throw new InvalidOperationException("Projection measured consumption changed.");
            if (sample.AllocatedBytes <= 0 || sample.ThreadAfter <= sample.ThreadBefore)
                throw new InvalidOperationException("Projection measurement lost its owned output allocation.");
            if (sample.AllocatedBytes > maximumWindowBytes)
                throw new InvalidOperationException($"Projection raw allocation {sample.AllocatedBytes} B / {sample.Operations} operations exceeds the calibrated {maximumWindowBytes} B window budget.");
        }
        GC.KeepAlive(projection); GC.KeepAlive(expressions);
        Console.WriteLine("PASS projection raw allocation <=9438704 B / 8192 operations; framework output arrays included, business result absent only in this fixture.");
    }

    internal static void RunScratchAllocation(string output)
    {
        if (RuntimeFeature.IsDynamicCodeSupported || !OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Projection scratch allocation requires Linux x64 Native AOT.");
        const int warmup = 1024, operations = 4096, repeats = 3;
        var projection = CreateProjection(8);
        var expressions = Expressions(8);
        var samples = new ProjectionAllocationSample[repeats];
        var checksumPerOperation = Consume(EightExpressionsGolden, EightExpressionsGolden);
        long warmChecksum = 0;
        for (var i = 0; i < warmup; i++) warmChecksum += ScratchConsume(projection, expressions);
        if (warmChecksum != checksumPerOperation * warmup) throw new InvalidOperationException("Scratch allocation warmup changed.");
        _ = Stopwatch.GetTimestamp(); _ = GC.GetTotalAllocatedBytes(true); _ = GC.GetAllocatedBytesForCurrentThread();
        _ = GC.CollectionCount(0); _ = GC.CollectionCount(1); _ = GC.CollectionCount(2);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            var gen0Before = GC.CollectionCount(0); var gen1Before = GC.CollectionCount(1); var gen2Before = GC.CollectionCount(2);
            var started = Stopwatch.GetTimestamp(); var before = GC.GetTotalAllocatedBytes(true);
            var threadBefore = GC.GetAllocatedBytesForCurrentThread();
            long checksum = 0;
            for (var i = 0; i < operations; i++) checksum += ScratchConsume(projection, expressions);
            var threadAfter = GC.GetAllocatedBytesForCurrentThread(); var after = GC.GetTotalAllocatedBytes(true); var ended = Stopwatch.GetTimestamp();
            var gen0After = GC.CollectionCount(0); var gen1After = GC.CollectionCount(1); var gen2After = GC.CollectionCount(2);
            samples[repeat] = new(repeat, operations, before, after, after - before, threadBefore, threadAfter,
                gen0Before, gen0After, gen1Before, gen1After, gen2Before, gen2After, checksum, ended - started);
        }
        var receipt = new ProjectionAllocationReceipt("DB-Perf-01", "internal-projection-scratch-short-regression", true,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessId,
            warmup, EightExpressionsGolden.Length, 1152,
            "c5c02b3c5a772619bba00983070c9c3bf665863bf710eb51d8b1d9214158e073",
            0, 0, 0, "36b309cf2fbd5871668ccf231c05af34607d5c52401860be45a24c08bcd34eb2",
            "Raw whole-process cumulative allocation and GC endpoints, plus separate current-thread endpoints. Each synchronous call borrows bounded stack scratch, compares all fixed wire bytes and consumes them before return. Prebuilt metadata/binder and reporting are outside. No subtraction or public-query attribution.",
            "No business result is produced by this fixture. Borrowed fitting projection frames are library internals; larger projections retain allocated fallback. GC counts belong to the shared process heap.",
            "Budget per 4096-operation window is the maximum raw whole-process count across three independent AOT processes with three windows each plus the observed range: 0 + 0 = 0 B. Current-thread endpoints also remain zero; no background bytes are subtracted.",
            0, true, Stopwatch.Frequency, samples);
        File.WriteAllText(Path.Combine(output, "projection-scratch-allocation.json"),
            JsonSerializer.Serialize(receipt, ProjectionTestJson.Default.ProjectionAllocationReceipt));
        foreach (var sample in samples)
        {
            if (sample.Checksum != checksumPerOperation * operations) throw new InvalidOperationException("Scratch measured consumption changed.");
            if (sample.AllocatedBytes != 0 || sample.ThreadAfter != sample.ThreadBefore)
                throw new InvalidOperationException($"Scratch encoding allocated {sample.AllocatedBytes} whole-process B / {sample.Operations} operations; the calibrated budget is 0 B.");
        }
        GC.KeepAlive(projection); GC.KeepAlive(expressions);
        Console.WriteLine("PASS scratch allocation 0 whole-process B / 4096 operations in the scoped synchronous prebuilt encoder; public-query allocation remains separate.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ScratchConsume(Projection<long> projection, ulong[] expressions)
    {
        Span<byte> scratch = stackalloc byte[OperandEncoding.StackScratchBytes];
        var bytes = OperandEncoding.ProjectionInto(projection, Bind, scratch, expressions);
        if (!bytes.SequenceEqual(EightExpressionsGolden)) throw new InvalidOperationException("Complete scratch projection wire changed.");
        long checksum = 0; foreach (var value in bytes) checksum += value; return checksum;
    }

    private static ResultField[] CreateFields(int count)
    {
        var fields = new ResultField[count];
        for (var i = 0; i < count; i++)
        {
            var ordinal = i.ToString(CultureInfo.InvariantCulture);
            var name = i % 2 == 0 ? "field_" + ordinal : "订单_" + ordinal + "_🦀";
            fields[i] = (i % 4) switch
            {
                0 => ResultField.Create(name, Codecs.Int64),
                1 => ResultField.Create(name, Codecs.NullableReference(Codecs.String)),
                2 => ResultField.Create(name, Codecs.Decimal(18, 2)),
                _ => ResultField.Create(name, Codecs.Boolean),
            };
        }
        return fields;
    }
    private static Projection<long> CreateProjection(int count) => Projection<long>.Create(CreateFields(count), static _ => 0L);
    private static ulong[] Expressions(int count)
    {
        var expressions = new ulong[count];
        for (var i = 0; i < count; i++) expressions[i] = 0x100000001UL + (uint)i;
        return expressions;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Consume(byte[] bytes, byte[] expected)
    {
        if (!bytes.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Complete projection wire changed.");
        long checksum = 0; foreach (var value in bytes) checksum += value; return checksum;
    }
}

internal readonly record struct ProjectionAllocationSample(int Repeat, int Operations,
    long AllocatedBefore, long AllocatedAfter, long AllocatedBytes, long ThreadBefore, long ThreadAfter,
    int Gen0Before, int Gen0After, int Gen1Before, int Gen1After, int Gen2Before, int Gen2After,
    long Checksum, long ElapsedTicks);
internal sealed record ProjectionAllocationReceipt(string Task, string Kind, bool NativeAot,
    string Runtime, string Architecture, int ProcessId, int WarmupOperations, int EncodedBytes,
    int MeasuredBeforeBytesPerOperation, string MicroComparisonSha256,
    long MeasuredMaximumWindowBytes, long MeasuredWindowRangeBytes, long MaximumWindowBytes,
    string CalibrationSummarySha256, string Scope, string AllocationAttribution,
    string BudgetStatus, int BusinessResultBytesPerOperation, bool WholeEncodeZeroBytes, long StopwatchFrequency,
    ProjectionAllocationSample[] Samples);
[JsonSerializable(typeof(ProjectionAllocationReceipt))]
internal partial class ProjectionTestJson : JsonSerializerContext { }
