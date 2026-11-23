using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Extensibility;
using Korda.Interop.Memory;

namespace Korda.Verification.Interop;

/// <summary>Independent mathematical/CLR oracles over actual compiled C, C++ and Core helpers.</summary>
/// <remarks>Numeric addresses are never dereferenced by extent tests. Owner tests use real creator allocations only.</remarks>
internal static class PropertySuite
{
    internal const ulong Seed = 0xa20c0dec0ffee001;
    internal const string GeneratorVersion = "splitmix64-a20-v1";

    internal static void Run(int cases = 4096)
    {
        Layout();
        Extents(cases);
        Utf8(cases / 4);
        Errors();
        Ownership(cases / 32);
    }

    private static unsafe void Layout()
    {
        NwAbiInfoV1 a = default;
        NwErrorInfoV1 e = default;
        NwBytesViewV1 b = default;
        NwOperationCallbacksV1 o = default;
        NwCallbacksV1 c = default;
        ulong[] actual = [
            (ulong)sizeof(NwAbiInfoV1), (ulong)sizeof(AlignAbi) - (ulong)sizeof(NwAbiInfoV1),
            Offset(&a, &a.StructSize), Offset(&a, &a.AbiMajor), Offset(&a, &a.AbiMinor), Offset(&a, &a.PointerWidthBits), Offset(&a, &a.Capabilities), Offset(&a, &a.Reserved),
            (ulong)sizeof(NwErrorInfoV1), (ulong)sizeof(AlignError) - (ulong)sizeof(NwErrorInfoV1),
            Offset(&e, &e.StructSize), Offset(&e, &e.Domain), Offset(&e, &e.Code), Offset(&e, &e.Flags), Offset(&e, &e.CorrelationId),
            (ulong)sizeof(NwBytesViewV1), (ulong)sizeof(AlignBytes) - (ulong)sizeof(NwBytesViewV1), Offset(&b, &b.Data), Offset(&b, &b.Length),
            (ulong)sizeof(NwOperationCallbacksV1), (ulong)sizeof(AlignOperation) - (ulong)sizeof(NwOperationCallbacksV1), Offset(&o, &o.StructSize), Offset(&o, &o.Flags), Offset(&o, &o.Cookie), Offset(&o, &o.OnTerminal),
            (ulong)sizeof(NwCallbacksV1), (ulong)sizeof(AlignCallback) - (ulong)sizeof(NwCallbacksV1), Offset(&c, &c.StructSize), Offset(&c, &c.Flags), Offset(&c, &c.Cookie), Offset(&c, &c.OnEvent)
        ];
        ulong[] contract = [32, 8, 0, 4, 8, 12, 16, 24, 24, 8, 0, 4, 8, 12, 16, 16, 8, 0, 8, 24, 8, 0, 4, 8, 16, 24, 8, 0, 4, 8, 16];
        for (uint i = 0; i < actual.Length; i++)
            Check.That(actual[i] == contract[i] && Native.Layout(i) == actual[i] && Native.CLayout(i) == actual[i], "layout field=" + i);
        Check.That(Native.Layout(31) == ulong.MaxValue && Native.CLayout(31) == ulong.MaxValue, "layout complete inventory");
        // Non-vacuity: an incorrect declared offset is observably different in all three compilers.
        Check.That(actual[7] != contract[7] + 1 && Native.Layout(7) != contract[7] + 1, "perturbed offset rejected");
    }
    private static unsafe ulong Offset(void* start, void* field) => (ulong)((byte*)field - (byte*)start);
    [StructLayout(LayoutKind.Sequential)] private struct AlignAbi { public byte Prefix; public NwAbiInfoV1 Value; }
    [StructLayout(LayoutKind.Sequential)] private struct AlignError { public byte Prefix; public NwErrorInfoV1 Value; }
    [StructLayout(LayoutKind.Sequential)] private struct AlignBytes { public byte Prefix; public NwBytesViewV1 Value; }
    [StructLayout(LayoutKind.Sequential)] private struct AlignOperation { public byte Prefix; public NwOperationCallbacksV1 Value; }
    [StructLayout(LayoutKind.Sequential)] private struct AlignCallback { public byte Prefix; public NwCallbacksV1 Value; }

    /// <summary>Unbounded BigInteger arithmetic provides the oracle independently from both checked implementations.</summary>
    internal static bool ExtentOracle(Extent d)
    {
        BigInteger first = (BigInteger)d.Address + d.Offset;
        BigInteger end = first + d.Length;
        return d.Width > 0 && d.Alignment > 0 && BigInteger.PopCount(d.Alignment) == 1 &&
            d.Address <= ulong.MaxValue && d.Capacity <= ulong.MaxValue &&
            d.Length <= long.MaxValue && d.Length <= d.Limit &&
            (BigInteger)d.Count * d.Width == d.Length &&
            (BigInteger)d.Offset + d.Length <= d.Capacity && first <= ulong.MaxValue && end <= ulong.MaxValue &&
            first % d.Alignment == 0 && (d.Address != 0 || (d.Length == 0 && d.Offset == 0));
    }

    internal static void Extents(int cases)
    {
        var random = new Sequence(Seed);
        ulong[] edges = [0, 1, 2, 7, 8, 15, 16, 31, 255, 65536, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue - 1, ulong.MaxValue];
        var known = new[] {
            new Extent(0, 0, 0, 1, 0, 0, 0, 1), new Extent(8, 16, 2, 8, 0, 16, 16, 8),
            new Extent(8, 16, 2, 8, 0, 15, 16, 8), new Extent(ulong.MaxValue, 1, 1, 1, 0, 1, 1, 1),
            new Extent(8, 0, 0, 0, 0, 0, 0, 1), new Extent(0, 0, 0, 1, 8, 8, 8, 8)
        };
        foreach (Extent d in known) ExtentCase(d);
        int valid = 0;
        for (int i = 0; i < cases; i++)
        {
            Extent d;
            if (i % 3 == 0)
            {
                uint alignment = 1U << (int)(random.Next() % 6);
                ulong width = 1 + random.Next() % 32, count = random.Next() % 4096, offset = random.Next() % 128 * alignment;
                ulong length = width * count;
                d = new Extent(4096 * alignment, length, count, width, offset, length + offset + random.Next() % 32, length + random.Next() % 32, alignment);
            }
            else
            {
                ulong Value() => i % 3 == 1 ? edges[random.Next() % (ulong)edges.Length] : random.Next();
                d = new Extent(Value(), Value(), Value(), Value(), Value(), Value(), Value(), (uint)Value());
            }
            ExtentCase(d);
            if (ExtentOracle(d)) valid++;
        }
        Check.That(valid > 0 && valid < cases, "extent generator has success and rejection");
        // A deliberately weakened capacity guard would accept this; the independent oracle must reject it.
        Check.That(!ExtentOracle(known[2]), "negative capacity guard oracle");
        Console.WriteLine($"PROPERTY extents={cases + known.Length} valid={valid} seed={Seed:x16} generator={GeneratorVersion}");
    }

    internal static void ExtentCase(Extent d)
    {
        bool expected = ExtentOracle(d);
        bool managed = ExtentValidator.TryValidate(d.Address, d.Length, d.Count, d.Width, d.Offset, d.Capacity, d.Limit, d.Alignment, out nuint managedStart);
        uint status = Native.Extent(d.Address, d.Length, d.Count, d.Width, d.Offset, d.Capacity, d.Limit, d.Alignment, out ulong nativeStart);
        ulong start = expected ? (ulong)((BigInteger)d.Address + d.Offset) : 0;
        Check.That(managed == expected && status == (expected ? 0U : 3U) && (ulong)managedStart == start && nativeStart == start, "extent:" + d.Serialize());
    }

    private static unsafe void Utf8(int cases)
    {
        var random = new Sequence(Seed ^ 0x8818);
        var strict = new UTF8Encoding(false, true);
        byte[][] fixedInputs = [[], [0], [0xc0, 0x80], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82], [0xff], Encoding.UTF8.GetBytes("A\0世界🙂e\u0301")];
        int accepted = 0, invalid = 0;
        for (int i = 0; i < cases + fixedInputs.Length; i++)
        {
            byte[] input = i < fixedInputs.Length ? fixedInputs[i] : i % 2 == 0 ? Encoding.UTF8.GetBytes("\0A世界🙂" + new string('x', i % 17)) : new byte[(int)(random.Next() % 48)];
            if (i >= fixedInputs.Length && i % 2 != 0) for (int j = 0; j < input.Length; j++) input[j] = (byte)random.Next();
            uint limit = (uint)(random.Next() % 65), capacity = (uint)(random.Next() % 65);
            byte[] output = Enumerable.Repeat((byte)0xa5, 80).ToArray();
            byte[] expected = []; bool valid = true;
            try
            {
                string text = strict.GetString(input);
                int budget = 0;
                foreach (Rune rune in text.EnumerateRunes()) { if (budget + rune.Utf8SequenceLength > limit) break; budget += rune.Utf8SequenceLength; }
                expected = input.AsSpan(0, budget).ToArray();
            }
            catch (DecoderFallbackException) { valid = false; }
            uint expectStatus = !valid ? 3U : expected.Length > capacity ? 7U : 0U;
            fixed (byte* source = input, destination = output)
            {
                uint status = Native.Utf8(source, (ulong)input.Length, limit, destination, capacity, out ulong written);
                Check.That(status == expectStatus && written == (status == 0 ? (ulong)expected.Length : 0), $"utf8:{Convert.ToHexString(input)}:{limit}:{capacity}");
                if (status == 0) { accepted++; Check.That(output.AsSpan(0, expected.Length).SequenceEqual(expected), "UTF8 bytes"); }
                else invalid++;
                int changed = status == 0 ? expected.Length : 0;
                Check.That(output.AsSpan(changed).IndexOfAnyExcept((byte)0xa5) < 0, "UTF8 no tail or failure writes");
            }
        }
        Check.That(accepted > 0 && invalid > 0, "UTF8 non-vacuity");
        Console.WriteLine($"PROPERTY utf8={cases + fixedInputs.Length} accepted={accepted} rejected={invalid}");
    }

    private static unsafe void Errors()
    {
        Span<ulong> aligned = stackalloc ulong[8];
        uint[] capacities = [0, 3, 4, 23, 24, 25, 32, 64];
        uint[] statuses = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, uint.MaxValue];
        foreach (uint status in statuses) foreach (uint capacity in capacities)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(aligned); bytes.Fill(0xa5);
            MemoryMarshal.Write(bytes, in capacity);
            byte[] original = bytes.ToArray();
            fixed (ulong* p = aligned)
            {
                Check.That(Native.Error(status, (NwErrorInfoV1*)p, uint.MaxValue, int.MinValue, ulong.MaxValue) == status, "error original status");
                if (status > 1 && capacity >= 24)
                {
                    NwErrorInfoV1 record = *(NwErrorInfoV1*)p;
                    Check.That(record.StructSize == 24 && record.Domain == uint.MaxValue && record.Code == int.MinValue && record.Flags == 0 && record.CorrelationId == ulong.MaxValue, "error complete fixed fields");
                    Check.That(bytes[24..].SequenceEqual(original.AsSpan(24)), "error tail canary");
                }
                else Check.That(bytes.SequenceEqual(original), "short error untouched");
            }
        }
        NwErrorInfoV1 error = new() { StructSize = 24, Domain = 17, Code = -71, CorrelationId = 123 };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        foreach (uint status in statuses.Where(s => s != 0))
        {
            Exception mapped = NativeError.Create("a20.fixture", "property", status, error, cancellation.Token);
            Type expected = status switch { 2 => typeof(OperationCanceledException), 3 => typeof(ArgumentException), 4 => typeof(InvalidOperationException), 6 => typeof(NotSupportedException), 12 => typeof(TimeoutException), _ => typeof(NativeEngineException) };
            Check.That(mapped.GetType() == expected, "exact status mapping " + status);
            if (mapped is OperationCanceledException canceled) Check.That(canceled.CancellationToken == cancellation.Token, "token identity");
        }
        error.Flags = 1;
        Check.That(NativeError.Create("a20.fixture", "bad flags", 2, error) is NativeEngineException, "malformed record cannot claim cancellation");
        error.Flags = 0; error.StructSize = 23;
        Check.That(NativeError.Create("a20.fixture", "short", 13, error) is NativeEngineException, "short record rejects domain mapping");
        Check.Throws<ArgumentException>(() => NativeError.Create("a20.fixture", "success", 0, error), "success has no error");
        Console.WriteLine("PROPERTY statuses=16 capacities=8 malformed-records=2");
    }

    private static unsafe void Ownership(int cases)
    {
        var random = new Sequence(Seed ^ 0x0ffe);
        for (int i = 0; i < cases; i++)
        {
            byte[] input = new byte[(int)(random.Next() % 128)];
            for (int j = 0; j < input.Length; j++) input[j] = (byte)random.Next();
            ulong reserve = random.Next() % 257;
            using (NativeBuffer buffer = MemoryNative.New(input, reserve))
            {
                Check.That(buffer.ToArray().AsSpan().SequenceEqual(input), "owned exact bytes");
                int offset = (int)(random.Next() % (ulong)(input.Length + 1));
                byte[] tail = new byte[input.Length - offset]; buffer.CopyTo(offset, tail);
                Check.That(tail.AsSpan().SequenceEqual(input.AsSpan(offset)), "owned range");
                if (input.Length > 0)
                {
                    byte[] shortBuffer = Enumerable.Repeat((byte)0xa5, input.Length - 1).ToArray();
                    Check.That(!buffer.TryCopyTo(shortBuffer) && shortBuffer.AsSpan().IndexOfAnyExcept((byte)0xa5) < 0, "short copy unchanged");
                }
                Check.Throws<ArgumentOutOfRangeException>(() => buffer.CopyTo(int.MaxValue, []), "offset checked before arithmetic");
                Check.Throws<FormatException>(() => buffer.Read<int>(_ => throw new FormatException("decoder")), "decoder failure releases lease");
                Check.That(buffer.Read(b => b.Length) == input.Length, "owner usable after decoder failure");
            }
            fixed (byte* p = input) for (uint fail = 1; fail <= 2; fail++)
            {
                Check.That(MemoryNative.Copy(p, (ulong)input.Length, reserve, fail, out nint output) == 5 && output == 0, "failed reserve/publication leaves owner empty");
            }
            using BufferHandle creator = MemoryNative.Create(input);
            Check.Throws<ArgumentOutOfRangeException>(() => NativeBufferFactory.Adopt(MemoryNative.Engine, creator, 0, -1), "adoption failure retains creator");
            Check.That(!creator.IsClosed, "failed adoption retains same creator");
        }
        Check.That(MemoryNative.Counter(0) == 0 && MemoryNative.Counter(2) == 0 && MemoryNative.Counter(4) == 0, "all memory owners/backings/capacity returned");
        Console.WriteLine($"PROPERTY owner-transfers={cases} native-owners=0 backings=0 capacity=0");
    }
}

/// <summary>Versioned scalar descriptor; colon-separated hexadecimal fields are replayed without pointers.</summary>
internal readonly record struct Extent(ulong Address, ulong Length, ulong Count, ulong Width, ulong Offset, ulong Capacity, ulong Limit, uint Alignment)
{
    internal string Serialize() => $"{Address:x16}:{Length:x16}:{Count:x16}:{Width:x16}:{Offset:x16}:{Capacity:x16}:{Limit:x16}:{Alignment:x8}";
    internal static Extent Parse(string value)
    {
        string[] p = value.Split(':');
        if (p.Length != 8) throw new FormatException("Extent-v1 requires eight hexadecimal fields.");
        ulong V(int i) => Convert.ToUInt64(p[i], 16);
        return new(V(0), V(1), V(2), V(3), V(4), V(5), V(6), checked((uint)V(7)));
    }
}
