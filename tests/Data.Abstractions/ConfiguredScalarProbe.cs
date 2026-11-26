using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Korda.Data;

// Production configured-scalar paths only. Descriptor construction/input bytes,
// differential correctness and reporting are outside the allocation intervals.
internal static class ConfiguredScalarProbe
{
    private const int Iterations = 100_000;
    private const int Warmup = 4096;
    private const int Repeats = 3;
    private static decimal _sink;

    internal static void Measure(string output, bool regression)
    {
        RequireAot();
        var money = Codecs.FixedScaleMoney(2);
        var moneyBytes = money.Encode(1234.56m)!;
        var configured = Codecs.Decimal(29, 2);
        const decimal number = 123456789012345678901234567.89m;
        var numberBytes = configured.Encode(number)!;
        var ordinary = Codecs.Decimal(18, 2);
        var ordinaryBytes = ordinary.Encode(12.34m)!;
        var inputs = AllConfiguredInputs();
        var cycle = 0;
        var probes = new (string Name, string Boundary, Action Run)[]
        {
            ("empty-arithmetic-control", "Int64 payload read; no framework call", () => _sink = BinaryPrimitives.ReadInt64LittleEndian(moneyBytes)),
            ("money-configuration", "CodecRegistry.Validate; cached FixedScaleMoney(2) descriptor", () => CodecRegistry.Validate(money.Descriptor)),
            ("money-wire-scalar", "DbWire.ValidateScalar; cached FixedScaleMoney(2), eight-byte input", () => DbWire.ValidateScalar(money.Descriptor, moneyBytes)),
            ("money-codec-decode-control", "Cached FixedScaleMoney(2).DecodeScalar; decimal result consumed", () => _sink = money.DecodeScalar(moneyBytes)),
            ("decimal-configuration", "CodecRegistry.Validate; cached Decimal(29,2) descriptor", () => CodecRegistry.Validate(configured.Descriptor)),
            ("decimal-wire-scalar", "DbWire.ValidateScalar; Decimal(29,2), canonical 96-bit input", () => DbWire.ValidateScalar(configured.Descriptor, numberBytes)),
            ("decimal-codec-decode", "Cached Decimal(29,2).DecodeScalar; decimal result consumed", () => _sink = configured.DecodeScalar(numberBytes)),
            ("decimal-precision-fit", "DecimalBytes.CheckFit; precision 29, scale 2, 96-bit value", () => DecimalBytes.CheckFit(number, 29, 2)),
            ("decimal-small-wire-scalar", "DbWire.ValidateScalar; Decimal(18,2), canonical 12.34", () => DbWire.ValidateScalar(ordinary.Descriptor, ordinaryBytes)),
            ("decimal96-decode-control", "Unconfigured DecimalBytes.Decode; existing canonical decode", () => _sink = DecimalBytes.Decode(numberBytes)),
            ("all-configurations-wire-scalar", "Round-robin all 463 Decimal and 19 FixedScaleMoney configurations; bounded prepared inputs", () =>
            {
                var input = inputs[cycle];
                DbWire.ValidateScalar(input.Descriptor, input.Bytes);
                cycle = (cycle + 1) % inputs.Length;
            }),
        };
        var results = new Window[probes.Length * Repeats];
        foreach (var probe in probes)
            for (var i = 0; i < Warmup; i++) probe.Run();
        _ = GC.GetTotalAllocatedBytes(true);
        _ = GC.CollectionCount(0); _ = GC.CollectionCount(1); _ = GC.CollectionCount(2);
        _ = Stopwatch.GetTimestamp();
        var index = 0;
        foreach (var probe in probes)
            for (var repeat = 0; repeat < Repeats; repeat++)
            {
                var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
                var start = Stopwatch.GetTimestamp();
                var before = GC.GetTotalAllocatedBytes(true);
                for (var operation = 0; operation < Iterations; operation++) probe.Run();
                var after = GC.GetTotalAllocatedBytes(true);
                var end = Stopwatch.GetTimestamp();
                results[index++] = new(probe.Name, probe.Boundary, repeat, before, after,
                    gc0, GC.CollectionCount(0), gc1, GC.CollectionCount(1), gc2, GC.CollectionCount(2), end - start);
            }
        var path = Path.Combine(output, "configured-scalar-allocation.json");
        using (var file = File.Create(path))
        using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            Header(json, "internal-configured-scalar-allocation");
            json.WriteNumber("operations_per_window", Iterations); json.WriteNumber("warmup_per_lane", Warmup);
            json.WriteNumber("windows_per_lane", Repeats); json.WriteNumber("configured_inputs", inputs.Length);
            json.WriteBoolean("regression_requested", regression);
            json.WriteString("boundary", "Whole-process GC.GetTotalAllocatedBytes(true); cached descriptors and prepared byte inputs. No business DTO, array or string result. Delegate/harness/runtime allocations remain included; no subtraction. Config factories, input generation, observations, SHA, JSON and exception/correctness matrices are outside the intervals.");
            json.WriteString("scope_limit", "This is not an entire query, batch, provider pipeline, caller configuration factory or whole-framework zero-allocation claim.");
            json.WriteString("allocation_attribution", "Raw end-to-end allocation of this isolated internal call and harness; no estimated subtraction.");
            json.WriteStartArray("windows");
            foreach (var result in results)
            {
                json.WriteStartObject(); json.WriteString("name", result.Name); json.WriteString("boundary", result.Boundary);
                json.WriteNumber("repeat", result.Repeat); json.WriteNumber("operations", Iterations);
                json.WriteNumber("allocated_before", result.Before); json.WriteNumber("allocated_after", result.After);
                json.WriteNumber("allocated_bytes_total", result.After - result.Before);
                json.WriteNumber("gc0_before", result.Gc0Before); json.WriteNumber("gc0_after", result.Gc0After);
                json.WriteNumber("gc1_before", result.Gc1Before); json.WriteNumber("gc1_after", result.Gc1After);
                json.WriteNumber("gc2_before", result.Gc2Before); json.WriteNumber("gc2_after", result.Gc2After);
                json.WriteNumber("elapsed_ticks", result.ElapsedTicks); json.WriteNumber("timer_frequency", Stopwatch.Frequency);
                json.WriteNumber("bytes_per_operation", (result.After - result.Before) / (double)Iterations);
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        Console.WriteLine("CONFIGURED_SCALAR_ALLOCATION_RESULT " + path);
        GC.KeepAlive(_sink);
        if (regression)
            foreach (var result in results)
                if (result.After != result.Before)
                    throw new InvalidOperationException($"{result.Name}: {result.After - result.Before} raw bytes / {Iterations} operations; exact zero required.");
    }

    internal static void Verify(string output)
    {
        RequireAot();
        var valid = 0; var fitCases = 0; var configurationCases = 0; var malformedPayloads = 0;
        var maximum = (BigInteger.One << 96) - 1;
        ulong random = 0xdeadbeef12345678;
        for (byte precision = 1; precision <= 29; precision++)
            for (byte scale = 0; scale <= Math.Min((byte)28, precision); scale++)
            {
                var codec = Codecs.Decimal(precision, scale);
                CompareConfiguration(codec.Descriptor, ref configurationCases);
                CompareConfiguration(codec.Descriptor with { Name = "nonsemantic-name", Nullable = true }, ref configurationCases);
                var power = BigInteger.Pow(10, precision);
                var boundary = BigInteger.Min(maximum, power - 1);
                var values = new List<decimal>
                {
                    0m, new decimal(0, 0, 0, true, 28), 1m, -1m,
                    decimal.MaxValue, decimal.MinValue,
                    MakeDecimal(boundary, scale, false), MakeDecimal(boundary, scale, true),
                    MakeDecimal(BigInteger.One << 32, scale, false),
                    MakeDecimal(BigInteger.One << 64, scale, true),
                    MakeDecimal(uint.MaxValue, 28, false),
                };
                if (power <= maximum)
                {
                    values.Add(MakeDecimal(power, scale, false));
                    values.Add(MakeDecimal(power, scale, true));
                }
                if (scale < 28)
                {
                    values.Add(MakeDecimal(1, (byte)(scale + 1), false));
                    values.Add(MakeDecimal(10, (byte)(scale + 1), true));
                }
                for (var i = 0; i < 4; i++)
                {
                    random ^= random << 13; random ^= random >> 7; random ^= random << 17;
                    var low = random;
                    random ^= random << 13; random ^= random >> 7; random ^= random << 17;
                    var coefficient = (BigInteger)(uint)random << 64 | low;
                    values.Add(MakeDecimal(coefficient, (byte)(random % 29), (random & 1) != 0));
                }
                foreach (var value in values)
                {
                    var expected = Capture(() => OracleFit(value, precision, scale));
                    Equal(expected, Capture(() => DecimalBytes.CheckFit(value, precision, scale)), "precision fit");
                    var bytes = DecimalBytes.Encode(value);
                    Equal(expected, Capture(() => DbWire.ValidateScalar(codec.Descriptor, bytes)), "wire scalar fit");
                    Equal(expected, Capture(() =>
                    {
                        var decoded = codec.DecodeScalar(bytes);
                        if (decoded != value) throw new InvalidOperationException("Configured decimal value changed.");
                    }), "configured codec decode");
                    fitCases += 3;
                }
                valid++;
            }
        for (byte scale = 0; scale <= 18; scale++)
        {
            var codec = Codecs.FixedScaleMoney(scale);
            CompareConfiguration(codec.Descriptor, ref configurationCases);
            CompareConfiguration(codec.Descriptor with { Name = "nonsemantic-name", Nullable = true }, ref configurationCases);
            foreach (var number in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue })
            {
                var bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, number);
                DbWire.ValidateScalar(codec.Descriptor, bytes);
                var expected = number / DecimalBytes.Power10(scale);
                if (codec.DecodeScalar(bytes) != expected) throw new InvalidOperationException("Money value changed.");
                valid++;
            }
        }
        string?[] malformed =
        [
            null, "", "{}", "null", "true", "[]", "0", "\"codec\"",
            "{\"precision\":18,\"scale\":2}", "{\"scale\":2}",
            " {\"precision\":18,\"scale\":2}", "{\"precision\":18,\"scale\":2} ",
            "{\"precision\": 18,\"scale\":2}", "{\"scale\":2,\"precision\":18}",
            "{\"precision\":18,\"scale\":2,\"x\":0}", "{\"precision\":18,\"precision\":18,\"scale\":2}",
            "{\"precision\":0,\"scale\":0}", "{\"precision\":30,\"scale\":0}",
            "{\"precision\":255,\"scale\":0}", "{\"precision\":256,\"scale\":0}",
            "{\"precision\":18,\"scale\":19}", "{\"precision\":29,\"scale\":29}",
            "{\"precision\":-1,\"scale\":0}", "{\"precision\":18.0,\"scale\":2}",
            "{\"precision\":18e0,\"scale\":2}", "{\"precision\":018,\"scale\":2}",
            "{\"precision\":\"18\",\"scale\":2}", "{\"precision\":null,\"scale\":2}",
            "{\"Precision\":18,\"scale\":2}", "{\"\\u0070recision\":18,\"scale\":2}",
            "{\"precision\":18,\"scale\":2,}", "{\"precision\":18,\"scale\":2}//x",
            "{\"precision\":18,\"scale\":2}{}", "{\"precision\":18,\"scale\":2}\0",
            "{\"scale\":-1}", "{\"scale\":19}", "{\"scale\":256}", "{\"scale\":2.0}",
            "{\"scale\":2e0}", "{\"scale\":02}", "{\"scale\":\"2\"}", "{\"scale\":null}",
            " {\"scale\":2}", "{\"scale\":2} ", "{\"scale\":2,\"scale\":2}",
            "{\"scale\":2,\"x\":0}", "{\"\\u0073cale\":2}", "{\"scale\":2}\ud800",
        ];
        foreach (var id in new uint[] { 13, 15 })
            foreach (var configuration in malformed)
                CompareConfiguration(new(id, "test", configuration, false), ref configurationCases);
        for (uint id = 0; id <= 18; id++)
        {
            CompareConfiguration(new(id, "test", null, false), ref configurationCases);
            CompareConfiguration(new(id, "test", "{}", false), ref configurationCases);
        }
        var descriptor = Codecs.Decimal(29, 2).Descriptor;
        var canonical = DecimalBytes.Encode(1.23m);
        for (var length = 0; length <= 17; length++)
            if (length != 16)
            {
                var bytes = new byte[length];
                Equal(Capture(() => LegacyScalar(descriptor, bytes)), Capture(() => DbWire.ValidateScalar(descriptor, bytes)), "decimal width");
                malformedPayloads++;
            }
        foreach (var mutation in new[] { (12, 29), (13, 2), (14, 1), (15, 1) })
        {
            var bytes = (byte[])canonical.Clone(); bytes[mutation.Item1] = (byte)mutation.Item2;
            Equal(Capture(() => LegacyScalar(descriptor, bytes)), Capture(() => DbWire.ValidateScalar(descriptor, bytes)), "decimal encoding");
            malformedPayloads++;
        }
        var path = Path.Combine(output, "configured-scalar-correctness.json");
        using (var file = File.Create(path))
        using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject(); Header(json, "configured-scalar-differential-correctness");
            json.WriteString("status", "passed"); json.WriteNumber("decimal_configurations", 463);
            json.WriteNumber("money_configurations", 19); json.WriteNumber("valid_configuration_or_money_values", valid);
            json.WriteNumber("precision_fit_comparisons", fitCases); json.WriteNumber("configuration_exception_comparisons", configurationCases);
            json.WriteNumber("malformed_payload_comparisons", malformedPayloads);
            json.WriteString("oracle", "Independent arbitrary-precision coefficient/scale arithmetic, original JSON-plus-codec canonical configuration validation, exact exception type/message/parameter comparison. Numeric equality checked after successful decode.");
            json.WriteEndObject();
        }
        Console.WriteLine("CONFIGURED_SCALAR_CORRECTNESS_RESULT " + path);
    }

    private static Input[] AllConfiguredInputs()
    {
        var inputs = new List<Input>();
        var maximum = (BigInteger.One << 96) - 1;
        for (byte precision = 1; precision <= 29; precision++)
            for (byte scale = 0; scale <= Math.Min((byte)28, precision); scale++)
            {
                var codec = Codecs.Decimal(precision, scale);
                var value = MakeDecimal(BigInteger.Min(maximum, BigInteger.Pow(10, precision) - 1), scale, false);
                inputs.Add(new(codec.Descriptor, codec.Encode(value)!));
            }
        for (byte scale = 0; scale <= 18; scale++)
        {
            var bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, long.MaxValue);
            inputs.Add(new(Codecs.FixedScaleMoney(scale).Descriptor, bytes));
        }
        if (inputs.Count != 482) throw new InvalidOperationException("Configured input coverage differs.");
        return inputs.ToArray();
    }

    private static decimal MakeDecimal(BigInteger coefficient, byte scale, bool negative)
        => new(unchecked((int)(uint)(coefficient & uint.MaxValue)),
            unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
            unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue)), negative, scale);

    private static void OracleFit(decimal value, byte precision, byte targetScale)
    {
        Span<int> bits = stackalloc int[4]; decimal.GetBits(value, bits);
        BigInteger coefficient = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        var scale = (byte)((bits[3] >> 16) & 255);
        if (coefficient.IsZero) scale = 0;
        else while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (scale > targetScale) throw new ArgumentException("Conversion would round the value.");
        if (coefficient * BigInteger.Pow(10, targetScale - scale) >= BigInteger.Pow(10, precision))
            throw new OverflowException("Decimal exceeds configured precision.");
    }

    private static void LegacyConfiguration(CodecDescriptor codec)
    {
        if (codec.Id is < 1 or > 17) throw new ArgumentException("Unknown required logical codec.");
        if (codec.Id is 13 or 15)
        {
            if (codec.Configuration is null) throw new ArgumentException("Configured codec requires configuration.");
            using var json = JsonDocument.Parse(codec.Configuration); var value = json.RootElement;
            string canonical;
            if (codec.Id == 13)
            {
                var p = value.GetProperty("precision").GetByte(); var s = value.GetProperty("scale").GetByte();
                canonical = Codecs.Decimal(p, s).Descriptor.Configuration!;
            }
            else { var s = value.GetProperty("scale").GetByte(); canonical = Codecs.FixedScaleMoney(s).Descriptor.Configuration!; }
            if (canonical != codec.Configuration) throw new ArgumentException("Unknown, duplicate or noncanonical codec configuration.");
        }
        else if (codec.Configuration is not null) throw new ArgumentException("This codec has no configuration.");
    }

    private static void LegacyScalar(CodecDescriptor codec, byte[] bytes)
    {
        LegacyConfiguration(codec);
        var value = DecimalBytes.Decode(bytes);
        using var config = JsonDocument.Parse(codec.Configuration!);
        OracleFit(value, config.RootElement.GetProperty("precision").GetByte(), config.RootElement.GetProperty("scale").GetByte());
    }

    private static void CompareConfiguration(CodecDescriptor codec, ref int count)
    {
        Equal(Capture(() => LegacyConfiguration(codec)), Capture(() => CodecRegistry.Validate(codec)), "configuration");
        count++;
    }

    private static Error? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return new(error.GetType().FullName!, error.Message, (error as ArgumentException)?.ParamName); }
    }

    private static void Equal(Error? expected, Error? actual, string boundary)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{boundary}: expected {expected}; observed {actual}.");
    }

    private static void Header(Utf8JsonWriter json, string scope)
    {
        json.WriteNumber("schema_version", 1); json.WriteString("task", "DB-Perf-01"); json.WriteString("scope", scope);
        json.WriteBoolean("native_aot", !RuntimeFeature.IsDynamicCodeSupported); json.WriteNumber("process_id", Environment.ProcessId);
        json.WriteString("runtime", RuntimeInformation.FrameworkDescription); json.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        json.WriteString("culture", CultureInfo.CurrentCulture.Name);
        json.WriteString("executable", Environment.ProcessPath);
        using var executable = File.OpenRead(Environment.ProcessPath!);
        json.WriteString("executable_sha256", Convert.ToHexStringLower(SHA256.HashData(executable)));
    }

    private static void RequireAot()
    {
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Configured scalar evidence requires Native AOT.");
    }

    private readonly record struct Input(CodecDescriptor Descriptor, byte[] Bytes);
    private readonly record struct Error(string Type, string Message, string? Parameter);
    private readonly record struct Window(string Name, string Boundary, int Repeat, long Before, long After,
        int Gc0Before, int Gc0After, int Gc1Before, int Gc1After, int Gc2Before, int Gc2After, long ElapsedTicks);
}
