using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NativeWeave.Server.Mcp;

internal static class SchemaFixtures
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(UnboundedAttribute))]
    internal static void Run(Action<bool, string> check)
    {
        void Reject<T>(JsonTypeInfo<T> info, string name)
        {
            try { StaticSchema.Create(name, info); }
            catch (ArgumentException error) when (error.Message.Contains("NWAPI-S004", StringComparison.Ordinal)) { check(StaticSchema.ScratchBytes == 0, "Schema rejects " + name + " without retained scratch"); return; }
            throw new InvalidOperationException("Schema unexpectedly admitted " + name);
        }
        Reject(SchemaJson.Default.Wide257, "property257");
        Reject(SchemaJson.Default.RepeatedRoot, "repeated-export-context");
        Reject(SchemaJson.Default.Recursive, "recursive");
        Reject(SchemaJson.Default.Open, "open");
        Reject(SchemaJson.Default.DictionaryDto, "dictionary");
        Reject(SchemaJson.Default.Attributed, "unbounded-attribute");
        check(UnboundedAttribute.Executions == 0, "Unknown attribute constructor never executes during schema registration");
        Reject(SchemaJson.Default.Deep01, "depth17");
        Reject(SchemaJson.Default.ManyTypes, "types65");
        Reject(SchemaJson.Default.ManyEnumDto, "enum257");
        foreach (var name in new[] { "wide256", "empty", "enum-numeric", "enum-string" })
        {
            Func<byte[]> create = name switch
            {
                "wide256" => () => StaticSchema.Create(name, SchemaJson.Default.Wide256),
                "empty" => () => StaticSchema.Create(name, SchemaJson.Default.Empty),
                "enum-numeric" => () => StaticSchema.Create(name, SchemaJson.Default.EnumDto),
                _ => () => StaticSchema.Create(name, StringEnumJson.Default.EnumDto)
            };
            create(); // Static caller metadata and runtime code are initialized separately.
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] schema = create();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"schema case={name} allocated={allocated} admitted={StaticSchema.LastScratchBytes} encoded={schema.Length}");
            check(allocated <= StaticSchema.LastScratchBytes && schema.Length <= 65536 && StaticSchema.ScratchBytes == 0, "Measured startup exporter allocation fits admitted scratch: " + name);
            using var document = JsonDocument.Parse(schema);
            if (name == "enum-string") check(document.RootElement.GetProperty("properties").GetProperty("value").GetProperty("enum").EnumerateArray().Any(v => v.GetString() == "renamed-value"), "Static renamed enum field survives schema export and Native AOT trimming");
        }
        check(JsonSerializer.Deserialize("{\"value\":\"renamed-value\"}", StringEnumJson.Default.EnumDto)!.Value == Choice.Second, "Static string enum codec agrees with schema");
        using var sparseSchema = JsonDocument.Parse(StaticSchema.Create("sparse-wide", SchemaJson.Default.WideArray));
        byte[] wire = System.Text.Encoding.UTF8.GetBytes("{\"items\":[" + string.Join(',', Enumerable.Repeat("{}", 320)) + "]}");
        _ = JsonSerializer.Deserialize(wire, SchemaJson.Default.WideArray);
        long start = GC.GetAllocatedBytesForCurrentThread();
        var sparse = JsonSerializer.Deserialize(wire, SchemaJson.Default.WideArray)!;
        long materialized = GC.GetAllocatedBytesForCurrentThread() - start;
        long managedBound = 3L * wire.Length + sparseSchema.RootElement.GetProperty(ManagedLayout.Annotation).GetProperty("nodeBytes").GetInt64() * 323 + 8 * 5 + 65536;
        check(materialized <= managedBound && sparse.Items.Length == 320, "Sparse wide static DTO storage fits managed per-node admission");
        Console.WriteLine($"managed_peak case=sparse-wide wire={wire.Length} nodes=323 allocated={materialized} bound={managedBound}");
        var options = new McpEndpointOptions(); options.AllowedHosts.Add("localhost:80"); options.AllowedOrigins.Add("https://localhost:443");
        _ = new McpDescriptor([], options); check(true, "Explicit default ports remain valid exact allowlist authorities");
    }
}
internal sealed class Empty;
internal sealed class Wide256
{
    public string? P000 { get; set; }
    public string? P001 { get; set; }
    public string? P002 { get; set; }
    public string? P003 { get; set; }
    public string? P004 { get; set; }
    public string? P005 { get; set; }
    public string? P006 { get; set; }
    public string? P007 { get; set; }
    public string? P008 { get; set; }
    public string? P009 { get; set; }
    public string? P010 { get; set; }
    public string? P011 { get; set; }
    public string? P012 { get; set; }
    public string? P013 { get; set; }
    public string? P014 { get; set; }
    public string? P015 { get; set; }
    public string? P016 { get; set; }
    public string? P017 { get; set; }
    public string? P018 { get; set; }
    public string? P019 { get; set; }
    public string? P020 { get; set; }
    public string? P021 { get; set; }
    public string? P022 { get; set; }
    public string? P023 { get; set; }
    public string? P024 { get; set; }
    public string? P025 { get; set; }
    public string? P026 { get; set; }
    public string? P027 { get; set; }
    public string? P028 { get; set; }
    public string? P029 { get; set; }
    public string? P030 { get; set; }
    public string? P031 { get; set; }
    public string? P032 { get; set; }
    public string? P033 { get; set; }
    public string? P034 { get; set; }
    public string? P035 { get; set; }
    public string? P036 { get; set; }
    public string? P037 { get; set; }
    public string? P038 { get; set; }
    public string? P039 { get; set; }
    public string? P040 { get; set; }
    public string? P041 { get; set; }
    public string? P042 { get; set; }
    public string? P043 { get; set; }
    public string? P044 { get; set; }
    public string? P045 { get; set; }
    public string? P046 { get; set; }
    public string? P047 { get; set; }
    public string? P048 { get; set; }
    public string? P049 { get; set; }
    public string? P050 { get; set; }
    public string? P051 { get; set; }
    public string? P052 { get; set; }
    public string? P053 { get; set; }
    public string? P054 { get; set; }
    public string? P055 { get; set; }
    public string? P056 { get; set; }
    public string? P057 { get; set; }
    public string? P058 { get; set; }
    public string? P059 { get; set; }
    public string? P060 { get; set; }
    public string? P061 { get; set; }
    public string? P062 { get; set; }
    public string? P063 { get; set; }
    public string? P064 { get; set; }
    public string? P065 { get; set; }
    public string? P066 { get; set; }
    public string? P067 { get; set; }
    public string? P068 { get; set; }
    public string? P069 { get; set; }
    public string? P070 { get; set; }
    public string? P071 { get; set; }
    public string? P072 { get; set; }
    public string? P073 { get; set; }
    public string? P074 { get; set; }
    public string? P075 { get; set; }
    public string? P076 { get; set; }
    public string? P077 { get; set; }
    public string? P078 { get; set; }
    public string? P079 { get; set; }
    public string? P080 { get; set; }
    public string? P081 { get; set; }
    public string? P082 { get; set; }
    public string? P083 { get; set; }
    public string? P084 { get; set; }
    public string? P085 { get; set; }
    public string? P086 { get; set; }
    public string? P087 { get; set; }
    public string? P088 { get; set; }
    public string? P089 { get; set; }
    public string? P090 { get; set; }
    public string? P091 { get; set; }
    public string? P092 { get; set; }
    public string? P093 { get; set; }
    public string? P094 { get; set; }
    public string? P095 { get; set; }
    public string? P096 { get; set; }
    public string? P097 { get; set; }
    public string? P098 { get; set; }
    public string? P099 { get; set; }
    public string? P100 { get; set; }
    public string? P101 { get; set; }
    public string? P102 { get; set; }
    public string? P103 { get; set; }
    public string? P104 { get; set; }
    public string? P105 { get; set; }
    public string? P106 { get; set; }
    public string? P107 { get; set; }
    public string? P108 { get; set; }
    public string? P109 { get; set; }
    public string? P110 { get; set; }
    public string? P111 { get; set; }
    public string? P112 { get; set; }
    public string? P113 { get; set; }
    public string? P114 { get; set; }
    public string? P115 { get; set; }
    public string? P116 { get; set; }
    public string? P117 { get; set; }
    public string? P118 { get; set; }
    public string? P119 { get; set; }
    public string? P120 { get; set; }
    public string? P121 { get; set; }
    public string? P122 { get; set; }
    public string? P123 { get; set; }
    public string? P124 { get; set; }
    public string? P125 { get; set; }
    public string? P126 { get; set; }
    public string? P127 { get; set; }
    public string? P128 { get; set; }
    public string? P129 { get; set; }
    public string? P130 { get; set; }
    public string? P131 { get; set; }
    public string? P132 { get; set; }
    public string? P133 { get; set; }
    public string? P134 { get; set; }
    public string? P135 { get; set; }
    public string? P136 { get; set; }
    public string? P137 { get; set; }
    public string? P138 { get; set; }
    public string? P139 { get; set; }
    public string? P140 { get; set; }
    public string? P141 { get; set; }
    public string? P142 { get; set; }
    public string? P143 { get; set; }
    public string? P144 { get; set; }
    public string? P145 { get; set; }
    public string? P146 { get; set; }
    public string? P147 { get; set; }
    public string? P148 { get; set; }
    public string? P149 { get; set; }
    public string? P150 { get; set; }
    public string? P151 { get; set; }
    public string? P152 { get; set; }
    public string? P153 { get; set; }
    public string? P154 { get; set; }
    public string? P155 { get; set; }
    public string? P156 { get; set; }
    public string? P157 { get; set; }
    public string? P158 { get; set; }
    public string? P159 { get; set; }
    public string? P160 { get; set; }
    public string? P161 { get; set; }
    public string? P162 { get; set; }
    public string? P163 { get; set; }
    public string? P164 { get; set; }
    public string? P165 { get; set; }
    public string? P166 { get; set; }
    public string? P167 { get; set; }
    public string? P168 { get; set; }
    public string? P169 { get; set; }
    public string? P170 { get; set; }
    public string? P171 { get; set; }
    public string? P172 { get; set; }
    public string? P173 { get; set; }
    public string? P174 { get; set; }
    public string? P175 { get; set; }
    public string? P176 { get; set; }
    public string? P177 { get; set; }
    public string? P178 { get; set; }
    public string? P179 { get; set; }
    public string? P180 { get; set; }
    public string? P181 { get; set; }
    public string? P182 { get; set; }
    public string? P183 { get; set; }
    public string? P184 { get; set; }
    public string? P185 { get; set; }
    public string? P186 { get; set; }
    public string? P187 { get; set; }
    public string? P188 { get; set; }
    public string? P189 { get; set; }
    public string? P190 { get; set; }
    public string? P191 { get; set; }
    public string? P192 { get; set; }
    public string? P193 { get; set; }
    public string? P194 { get; set; }
    public string? P195 { get; set; }
    public string? P196 { get; set; }
    public string? P197 { get; set; }
    public string? P198 { get; set; }
    public string? P199 { get; set; }
    public string? P200 { get; set; }
    public string? P201 { get; set; }
    public string? P202 { get; set; }
    public string? P203 { get; set; }
    public string? P204 { get; set; }
    public string? P205 { get; set; }
    public string? P206 { get; set; }
    public string? P207 { get; set; }
    public string? P208 { get; set; }
    public string? P209 { get; set; }
    public string? P210 { get; set; }
    public string? P211 { get; set; }
    public string? P212 { get; set; }
    public string? P213 { get; set; }
    public string? P214 { get; set; }
    public string? P215 { get; set; }
    public string? P216 { get; set; }
    public string? P217 { get; set; }
    public string? P218 { get; set; }
    public string? P219 { get; set; }
    public string? P220 { get; set; }
    public string? P221 { get; set; }
    public string? P222 { get; set; }
    public string? P223 { get; set; }
    public string? P224 { get; set; }
    public string? P225 { get; set; }
    public string? P226 { get; set; }
    public string? P227 { get; set; }
    public string? P228 { get; set; }
    public string? P229 { get; set; }
    public string? P230 { get; set; }
    public string? P231 { get; set; }
    public string? P232 { get; set; }
    public string? P233 { get; set; }
    public string? P234 { get; set; }
    public string? P235 { get; set; }
    public string? P236 { get; set; }
    public string? P237 { get; set; }
    public string? P238 { get; set; }
    public string? P239 { get; set; }
    public string? P240 { get; set; }
    public string? P241 { get; set; }
    public string? P242 { get; set; }
    public string? P243 { get; set; }
    public string? P244 { get; set; }
    public string? P245 { get; set; }
    public string? P246 { get; set; }
    public string? P247 { get; set; }
    public string? P248 { get; set; }
    public string? P249 { get; set; }
    public string? P250 { get; set; }
    public string? P251 { get; set; }
    public string? P252 { get; set; }
    public string? P253 { get; set; }
    public string? P254 { get; set; }
    public string? P255 { get; set; }
}
internal sealed class Wide257
{
    public string? P000 { get; set; }
    public string? P001 { get; set; }
    public string? P002 { get; set; }
    public string? P003 { get; set; }
    public string? P004 { get; set; }
    public string? P005 { get; set; }
    public string? P006 { get; set; }
    public string? P007 { get; set; }
    public string? P008 { get; set; }
    public string? P009 { get; set; }
    public string? P010 { get; set; }
    public string? P011 { get; set; }
    public string? P012 { get; set; }
    public string? P013 { get; set; }
    public string? P014 { get; set; }
    public string? P015 { get; set; }
    public string? P016 { get; set; }
    public string? P017 { get; set; }
    public string? P018 { get; set; }
    public string? P019 { get; set; }
    public string? P020 { get; set; }
    public string? P021 { get; set; }
    public string? P022 { get; set; }
    public string? P023 { get; set; }
    public string? P024 { get; set; }
    public string? P025 { get; set; }
    public string? P026 { get; set; }
    public string? P027 { get; set; }
    public string? P028 { get; set; }
    public string? P029 { get; set; }
    public string? P030 { get; set; }
    public string? P031 { get; set; }
    public string? P032 { get; set; }
    public string? P033 { get; set; }
    public string? P034 { get; set; }
    public string? P035 { get; set; }
    public string? P036 { get; set; }
    public string? P037 { get; set; }
    public string? P038 { get; set; }
    public string? P039 { get; set; }
    public string? P040 { get; set; }
    public string? P041 { get; set; }
    public string? P042 { get; set; }
    public string? P043 { get; set; }
    public string? P044 { get; set; }
    public string? P045 { get; set; }
    public string? P046 { get; set; }
    public string? P047 { get; set; }
    public string? P048 { get; set; }
    public string? P049 { get; set; }
    public string? P050 { get; set; }
    public string? P051 { get; set; }
    public string? P052 { get; set; }
    public string? P053 { get; set; }
    public string? P054 { get; set; }
    public string? P055 { get; set; }
    public string? P056 { get; set; }
    public string? P057 { get; set; }
    public string? P058 { get; set; }
    public string? P059 { get; set; }
    public string? P060 { get; set; }
    public string? P061 { get; set; }
    public string? P062 { get; set; }
    public string? P063 { get; set; }
    public string? P064 { get; set; }
    public string? P065 { get; set; }
    public string? P066 { get; set; }
    public string? P067 { get; set; }
    public string? P068 { get; set; }
    public string? P069 { get; set; }
    public string? P070 { get; set; }
    public string? P071 { get; set; }
    public string? P072 { get; set; }
    public string? P073 { get; set; }
    public string? P074 { get; set; }
    public string? P075 { get; set; }
    public string? P076 { get; set; }
    public string? P077 { get; set; }
    public string? P078 { get; set; }
    public string? P079 { get; set; }
    public string? P080 { get; set; }
    public string? P081 { get; set; }
    public string? P082 { get; set; }
    public string? P083 { get; set; }
    public string? P084 { get; set; }
    public string? P085 { get; set; }
    public string? P086 { get; set; }
    public string? P087 { get; set; }
    public string? P088 { get; set; }
    public string? P089 { get; set; }
    public string? P090 { get; set; }
    public string? P091 { get; set; }
    public string? P092 { get; set; }
    public string? P093 { get; set; }
    public string? P094 { get; set; }
    public string? P095 { get; set; }
    public string? P096 { get; set; }
    public string? P097 { get; set; }
    public string? P098 { get; set; }
    public string? P099 { get; set; }
    public string? P100 { get; set; }
    public string? P101 { get; set; }
    public string? P102 { get; set; }
    public string? P103 { get; set; }
    public string? P104 { get; set; }
    public string? P105 { get; set; }
    public string? P106 { get; set; }
    public string? P107 { get; set; }
    public string? P108 { get; set; }
    public string? P109 { get; set; }
    public string? P110 { get; set; }
    public string? P111 { get; set; }
    public string? P112 { get; set; }
    public string? P113 { get; set; }
    public string? P114 { get; set; }
    public string? P115 { get; set; }
    public string? P116 { get; set; }
    public string? P117 { get; set; }
    public string? P118 { get; set; }
    public string? P119 { get; set; }
    public string? P120 { get; set; }
    public string? P121 { get; set; }
    public string? P122 { get; set; }
    public string? P123 { get; set; }
    public string? P124 { get; set; }
    public string? P125 { get; set; }
    public string? P126 { get; set; }
    public string? P127 { get; set; }
    public string? P128 { get; set; }
    public string? P129 { get; set; }
    public string? P130 { get; set; }
    public string? P131 { get; set; }
    public string? P132 { get; set; }
    public string? P133 { get; set; }
    public string? P134 { get; set; }
    public string? P135 { get; set; }
    public string? P136 { get; set; }
    public string? P137 { get; set; }
    public string? P138 { get; set; }
    public string? P139 { get; set; }
    public string? P140 { get; set; }
    public string? P141 { get; set; }
    public string? P142 { get; set; }
    public string? P143 { get; set; }
    public string? P144 { get; set; }
    public string? P145 { get; set; }
    public string? P146 { get; set; }
    public string? P147 { get; set; }
    public string? P148 { get; set; }
    public string? P149 { get; set; }
    public string? P150 { get; set; }
    public string? P151 { get; set; }
    public string? P152 { get; set; }
    public string? P153 { get; set; }
    public string? P154 { get; set; }
    public string? P155 { get; set; }
    public string? P156 { get; set; }
    public string? P157 { get; set; }
    public string? P158 { get; set; }
    public string? P159 { get; set; }
    public string? P160 { get; set; }
    public string? P161 { get; set; }
    public string? P162 { get; set; }
    public string? P163 { get; set; }
    public string? P164 { get; set; }
    public string? P165 { get; set; }
    public string? P166 { get; set; }
    public string? P167 { get; set; }
    public string? P168 { get; set; }
    public string? P169 { get; set; }
    public string? P170 { get; set; }
    public string? P171 { get; set; }
    public string? P172 { get; set; }
    public string? P173 { get; set; }
    public string? P174 { get; set; }
    public string? P175 { get; set; }
    public string? P176 { get; set; }
    public string? P177 { get; set; }
    public string? P178 { get; set; }
    public string? P179 { get; set; }
    public string? P180 { get; set; }
    public string? P181 { get; set; }
    public string? P182 { get; set; }
    public string? P183 { get; set; }
    public string? P184 { get; set; }
    public string? P185 { get; set; }
    public string? P186 { get; set; }
    public string? P187 { get; set; }
    public string? P188 { get; set; }
    public string? P189 { get; set; }
    public string? P190 { get; set; }
    public string? P191 { get; set; }
    public string? P192 { get; set; }
    public string? P193 { get; set; }
    public string? P194 { get; set; }
    public string? P195 { get; set; }
    public string? P196 { get; set; }
    public string? P197 { get; set; }
    public string? P198 { get; set; }
    public string? P199 { get; set; }
    public string? P200 { get; set; }
    public string? P201 { get; set; }
    public string? P202 { get; set; }
    public string? P203 { get; set; }
    public string? P204 { get; set; }
    public string? P205 { get; set; }
    public string? P206 { get; set; }
    public string? P207 { get; set; }
    public string? P208 { get; set; }
    public string? P209 { get; set; }
    public string? P210 { get; set; }
    public string? P211 { get; set; }
    public string? P212 { get; set; }
    public string? P213 { get; set; }
    public string? P214 { get; set; }
    public string? P215 { get; set; }
    public string? P216 { get; set; }
    public string? P217 { get; set; }
    public string? P218 { get; set; }
    public string? P219 { get; set; }
    public string? P220 { get; set; }
    public string? P221 { get; set; }
    public string? P222 { get; set; }
    public string? P223 { get; set; }
    public string? P224 { get; set; }
    public string? P225 { get; set; }
    public string? P226 { get; set; }
    public string? P227 { get; set; }
    public string? P228 { get; set; }
    public string? P229 { get; set; }
    public string? P230 { get; set; }
    public string? P231 { get; set; }
    public string? P232 { get; set; }
    public string? P233 { get; set; }
    public string? P234 { get; set; }
    public string? P235 { get; set; }
    public string? P236 { get; set; }
    public string? P237 { get; set; }
    public string? P238 { get; set; }
    public string? P239 { get; set; }
    public string? P240 { get; set; }
    public string? P241 { get; set; }
    public string? P242 { get; set; }
    public string? P243 { get; set; }
    public string? P244 { get; set; }
    public string? P245 { get; set; }
    public string? P246 { get; set; }
    public string? P247 { get; set; }
    public string? P248 { get; set; }
    public string? P249 { get; set; }
    public string? P250 { get; set; }
    public string? P251 { get; set; }
    public string? P252 { get; set; }
    public string? P253 { get; set; }
    public string? P254 { get; set; }
    public string? P255 { get; set; }
    public string? P256 { get; set; }
}
internal sealed class Recursive { public Recursive? Next { get; set; } }
internal sealed record Open(object Value);
internal sealed record DictionaryDto(Dictionary<string, string> Values);
[Unbounded]
internal sealed record Attributed(string Value);
[AttributeUsage(AttributeTargets.Class)]
internal sealed class UnboundedAttribute : Attribute
{
    public static int Executions;
    public UnboundedAttribute() => Interlocked.Increment(ref Executions);
}
internal enum Choice { First, [JsonStringEnumMemberName("renamed-value")] Second }
internal sealed record EnumDto(Choice Value);
internal enum ManyEnum { V0, V1, V2, V3, V4, V5, V6, V7, V8, V9, V10, V11, V12, V13, V14, V15, V16, V17, V18, V19, V20, V21, V22, V23, V24, V25, V26, V27, V28, V29, V30, V31, V32, V33, V34, V35, V36, V37, V38, V39, V40, V41, V42, V43, V44, V45, V46, V47, V48, V49, V50, V51, V52, V53, V54, V55, V56, V57, V58, V59, V60, V61, V62, V63, V64, V65, V66, V67, V68, V69, V70, V71, V72, V73, V74, V75, V76, V77, V78, V79, V80, V81, V82, V83, V84, V85, V86, V87, V88, V89, V90, V91, V92, V93, V94, V95, V96, V97, V98, V99, V100, V101, V102, V103, V104, V105, V106, V107, V108, V109, V110, V111, V112, V113, V114, V115, V116, V117, V118, V119, V120, V121, V122, V123, V124, V125, V126, V127, V128, V129, V130, V131, V132, V133, V134, V135, V136, V137, V138, V139, V140, V141, V142, V143, V144, V145, V146, V147, V148, V149, V150, V151, V152, V153, V154, V155, V156, V157, V158, V159, V160, V161, V162, V163, V164, V165, V166, V167, V168, V169, V170, V171, V172, V173, V174, V175, V176, V177, V178, V179, V180, V181, V182, V183, V184, V185, V186, V187, V188, V189, V190, V191, V192, V193, V194, V195, V196, V197, V198, V199, V200, V201, V202, V203, V204, V205, V206, V207, V208, V209, V210, V211, V212, V213, V214, V215, V216, V217, V218, V219, V220, V221, V222, V223, V224, V225, V226, V227, V228, V229, V230, V231, V232, V233, V234, V235, V236, V237, V238, V239, V240, V241, V242, V243, V244, V245, V246, V247, V248, V249, V250, V251, V252, V253, V254, V255, V256 }
internal sealed record ManyEnumDto(ManyEnum Value);
internal sealed record Deep01(Deep02? Next);
internal sealed record Deep02(Deep03? Next);
internal sealed record Deep03(Deep04? Next);
internal sealed record Deep04(Deep05? Next);
internal sealed record Deep05(Deep06? Next);
internal sealed record Deep06(Deep07? Next);
internal sealed record Deep07(Deep08? Next);
internal sealed record Deep08(Deep09? Next);
internal sealed record Deep09(Deep10? Next);
internal sealed record Deep10(Deep11? Next);
internal sealed record Deep11(Deep12? Next);
internal sealed record Deep12(Deep13? Next);
internal sealed record Deep13(Deep14? Next);
internal sealed record Deep14(Deep15? Next);
internal sealed record Deep15(Deep16? Next);
internal sealed record Deep16(Deep17? Next);
internal sealed record Deep17(Deep18? Next);
internal sealed record Deep18(string Value);
internal sealed class ManyTypes
{
    public Distinct00? P00 { get; set; }
    public Distinct01? P01 { get; set; }
    public Distinct02? P02 { get; set; }
    public Distinct03? P03 { get; set; }
    public Distinct04? P04 { get; set; }
    public Distinct05? P05 { get; set; }
    public Distinct06? P06 { get; set; }
    public Distinct07? P07 { get; set; }
    public Distinct08? P08 { get; set; }
    public Distinct09? P09 { get; set; }
    public Distinct10? P10 { get; set; }
    public Distinct11? P11 { get; set; }
    public Distinct12? P12 { get; set; }
    public Distinct13? P13 { get; set; }
    public Distinct14? P14 { get; set; }
    public Distinct15? P15 { get; set; }
    public Distinct16? P16 { get; set; }
    public Distinct17? P17 { get; set; }
    public Distinct18? P18 { get; set; }
    public Distinct19? P19 { get; set; }
    public Distinct20? P20 { get; set; }
    public Distinct21? P21 { get; set; }
    public Distinct22? P22 { get; set; }
    public Distinct23? P23 { get; set; }
    public Distinct24? P24 { get; set; }
    public Distinct25? P25 { get; set; }
    public Distinct26? P26 { get; set; }
    public Distinct27? P27 { get; set; }
    public Distinct28? P28 { get; set; }
    public Distinct29? P29 { get; set; }
    public Distinct30? P30 { get; set; }
    public Distinct31? P31 { get; set; }
    public Distinct32? P32 { get; set; }
    public Distinct33? P33 { get; set; }
    public Distinct34? P34 { get; set; }
    public Distinct35? P35 { get; set; }
    public Distinct36? P36 { get; set; }
    public Distinct37? P37 { get; set; }
    public Distinct38? P38 { get; set; }
    public Distinct39? P39 { get; set; }
    public Distinct40? P40 { get; set; }
    public Distinct41? P41 { get; set; }
    public Distinct42? P42 { get; set; }
    public Distinct43? P43 { get; set; }
    public Distinct44? P44 { get; set; }
    public Distinct45? P45 { get; set; }
    public Distinct46? P46 { get; set; }
    public Distinct47? P47 { get; set; }
    public Distinct48? P48 { get; set; }
    public Distinct49? P49 { get; set; }
    public Distinct50? P50 { get; set; }
    public Distinct51? P51 { get; set; }
    public Distinct52? P52 { get; set; }
    public Distinct53? P53 { get; set; }
    public Distinct54? P54 { get; set; }
    public Distinct55? P55 { get; set; }
    public Distinct56? P56 { get; set; }
    public Distinct57? P57 { get; set; }
    public Distinct58? P58 { get; set; }
    public Distinct59? P59 { get; set; }
    public Distinct60? P60 { get; set; }
    public Distinct61? P61 { get; set; }
    public Distinct62? P62 { get; set; }
    public Distinct63? P63 { get; set; }
}
internal sealed class Distinct00;
internal sealed class Distinct01;
internal sealed class Distinct02;
internal sealed class Distinct03;
internal sealed class Distinct04;
internal sealed class Distinct05;
internal sealed class Distinct06;
internal sealed class Distinct07;
internal sealed class Distinct08;
internal sealed class Distinct09;
internal sealed class Distinct10;
internal sealed class Distinct11;
internal sealed class Distinct12;
internal sealed class Distinct13;
internal sealed class Distinct14;
internal sealed class Distinct15;
internal sealed class Distinct16;
internal sealed class Distinct17;
internal sealed class Distinct18;
internal sealed class Distinct19;
internal sealed class Distinct20;
internal sealed class Distinct21;
internal sealed class Distinct22;
internal sealed class Distinct23;
internal sealed class Distinct24;
internal sealed class Distinct25;
internal sealed class Distinct26;
internal sealed class Distinct27;
internal sealed class Distinct28;
internal sealed class Distinct29;
internal sealed class Distinct30;
internal sealed class Distinct31;
internal sealed class Distinct32;
internal sealed class Distinct33;
internal sealed class Distinct34;
internal sealed class Distinct35;
internal sealed class Distinct36;
internal sealed class Distinct37;
internal sealed class Distinct38;
internal sealed class Distinct39;
internal sealed class Distinct40;
internal sealed class Distinct41;
internal sealed class Distinct42;
internal sealed class Distinct43;
internal sealed class Distinct44;
internal sealed class Distinct45;
internal sealed class Distinct46;
internal sealed class Distinct47;
internal sealed class Distinct48;
internal sealed class Distinct49;
internal sealed class Distinct50;
internal sealed class Distinct51;
internal sealed class Distinct52;
internal sealed class Distinct53;
internal sealed class Distinct54;
internal sealed class Distinct55;
internal sealed class Distinct56;
internal sealed class Distinct57;
internal sealed class Distinct58;
internal sealed class Distinct59;
internal sealed class Distinct60;
internal sealed class Distinct61;
internal sealed class Distinct62;
internal sealed class Distinct63;
[JsonSerializable(typeof(RepeatedRoot))]
[JsonSerializable(typeof(WideArray))]
[JsonSerializable(typeof(Wide256))]
[JsonSerializable(typeof(Wide257))]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(EnumDto))]
[JsonSerializable(typeof(ManyEnumDto))]
[JsonSerializable(typeof(Recursive))]
[JsonSerializable(typeof(Open))]
[JsonSerializable(typeof(DictionaryDto))]
[JsonSerializable(typeof(Attributed))]
[JsonSerializable(typeof(Deep01))]
[JsonSerializable(typeof(ManyTypes))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class SchemaJson : JsonSerializerContext;
[JsonSerializable(typeof(EnumDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
internal partial class StringEnumJson : JsonSerializerContext;

internal sealed record WideArray(WideCell[] Items);
internal sealed class WideCell
{
    public string? P000 { get; set; }
    public string? P001 { get; set; }
    public string? P002 { get; set; }
    public string? P003 { get; set; }
    public string? P004 { get; set; }
    public string? P005 { get; set; }
    public string? P006 { get; set; }
    public string? P007 { get; set; }
    public string? P008 { get; set; }
    public string? P009 { get; set; }
    public string? P010 { get; set; }
    public string? P011 { get; set; }
    public string? P012 { get; set; }
    public string? P013 { get; set; }
    public string? P014 { get; set; }
    public string? P015 { get; set; }
    public string? P016 { get; set; }
    public string? P017 { get; set; }
    public string? P018 { get; set; }
    public string? P019 { get; set; }
    public string? P020 { get; set; }
    public string? P021 { get; set; }
    public string? P022 { get; set; }
    public string? P023 { get; set; }
    public string? P024 { get; set; }
    public string? P025 { get; set; }
    public string? P026 { get; set; }
    public string? P027 { get; set; }
    public string? P028 { get; set; }
    public string? P029 { get; set; }
    public string? P030 { get; set; }
    public string? P031 { get; set; }
    public string? P032 { get; set; }
    public string? P033 { get; set; }
    public string? P034 { get; set; }
    public string? P035 { get; set; }
    public string? P036 { get; set; }
    public string? P037 { get; set; }
    public string? P038 { get; set; }
    public string? P039 { get; set; }
    public string? P040 { get; set; }
    public string? P041 { get; set; }
    public string? P042 { get; set; }
    public string? P043 { get; set; }
    public string? P044 { get; set; }
    public string? P045 { get; set; }
    public string? P046 { get; set; }
    public string? P047 { get; set; }
    public string? P048 { get; set; }
    public string? P049 { get; set; }
    public string? P050 { get; set; }
    public string? P051 { get; set; }
    public string? P052 { get; set; }
    public string? P053 { get; set; }
    public string? P054 { get; set; }
    public string? P055 { get; set; }
    public string? P056 { get; set; }
    public string? P057 { get; set; }
    public string? P058 { get; set; }
    public string? P059 { get; set; }
    public string? P060 { get; set; }
    public string? P061 { get; set; }
    public string? P062 { get; set; }
    public string? P063 { get; set; }
    public string? P064 { get; set; }
    public string? P065 { get; set; }
    public string? P066 { get; set; }
    public string? P067 { get; set; }
    public string? P068 { get; set; }
    public string? P069 { get; set; }
    public string? P070 { get; set; }
    public string? P071 { get; set; }
    public string? P072 { get; set; }
    public string? P073 { get; set; }
    public string? P074 { get; set; }
    public string? P075 { get; set; }
    public string? P076 { get; set; }
    public string? P077 { get; set; }
    public string? P078 { get; set; }
    public string? P079 { get; set; }
    public string? P080 { get; set; }
    public string? P081 { get; set; }
    public string? P082 { get; set; }
    public string? P083 { get; set; }
    public string? P084 { get; set; }
    public string? P085 { get; set; }
    public string? P086 { get; set; }
    public string? P087 { get; set; }
    public string? P088 { get; set; }
    public string? P089 { get; set; }
    public string? P090 { get; set; }
    public string? P091 { get; set; }
    public string? P092 { get; set; }
    public string? P093 { get; set; }
    public string? P094 { get; set; }
    public string? P095 { get; set; }
    public string? P096 { get; set; }
    public string? P097 { get; set; }
    public string? P098 { get; set; }
    public string? P099 { get; set; }
    public string? P100 { get; set; }
    public string? P101 { get; set; }
    public string? P102 { get; set; }
    public string? P103 { get; set; }
    public string? P104 { get; set; }
    public string? P105 { get; set; }
    public string? P106 { get; set; }
    public string? P107 { get; set; }
    public string? P108 { get; set; }
    public string? P109 { get; set; }
    public string? P110 { get; set; }
    public string? P111 { get; set; }
    public string? P112 { get; set; }
    public string? P113 { get; set; }
    public string? P114 { get; set; }
    public string? P115 { get; set; }
    public string? P116 { get; set; }
    public string? P117 { get; set; }
    public string? P118 { get; set; }
    public string? P119 { get; set; }
    public string? P120 { get; set; }
    public string? P121 { get; set; }
    public string? P122 { get; set; }
    public string? P123 { get; set; }
    public string? P124 { get; set; }
    public string? P125 { get; set; }
    public string? P126 { get; set; }
    public string? P127 { get; set; }
    public string? P128 { get; set; }
    public string? P129 { get; set; }
    public string? P130 { get; set; }
    public string? P131 { get; set; }
    public string? P132 { get; set; }
    public string? P133 { get; set; }
    public string? P134 { get; set; }
    public string? P135 { get; set; }
    public string? P136 { get; set; }
    public string? P137 { get; set; }
    public string? P138 { get; set; }
    public string? P139 { get; set; }
    public string? P140 { get; set; }
    public string? P141 { get; set; }
    public string? P142 { get; set; }
    public string? P143 { get; set; }
    public string? P144 { get; set; }
    public string? P145 { get; set; }
    public string? P146 { get; set; }
    public string? P147 { get; set; }
    public string? P148 { get; set; }
    public string? P149 { get; set; }
    public string? P150 { get; set; }
    public string? P151 { get; set; }
    public string? P152 { get; set; }
    public string? P153 { get; set; }
    public string? P154 { get; set; }
    public string? P155 { get; set; }
    public string? P156 { get; set; }
    public string? P157 { get; set; }
    public string? P158 { get; set; }
    public string? P159 { get; set; }
    public string? P160 { get; set; }
    public string? P161 { get; set; }
    public string? P162 { get; set; }
    public string? P163 { get; set; }
    public string? P164 { get; set; }
    public string? P165 { get; set; }
    public string? P166 { get; set; }
    public string? P167 { get; set; }
    public string? P168 { get; set; }
    public string? P169 { get; set; }
    public string? P170 { get; set; }
    public string? P171 { get; set; }
    public string? P172 { get; set; }
    public string? P173 { get; set; }
    public string? P174 { get; set; }
    public string? P175 { get; set; }
    public string? P176 { get; set; }
    public string? P177 { get; set; }
    public string? P178 { get; set; }
    public string? P179 { get; set; }
    public string? P180 { get; set; }
    public string? P181 { get; set; }
    public string? P182 { get; set; }
    public string? P183 { get; set; }
    public string? P184 { get; set; }
    public string? P185 { get; set; }
    public string? P186 { get; set; }
    public string? P187 { get; set; }
    public string? P188 { get; set; }
    public string? P189 { get; set; }
    public string? P190 { get; set; }
    public string? P191 { get; set; }
    public string? P192 { get; set; }
    public string? P193 { get; set; }
    public string? P194 { get; set; }
    public string? P195 { get; set; }
    public string? P196 { get; set; }
    public string? P197 { get; set; }
    public string? P198 { get; set; }
    public string? P199 { get; set; }
    public string? P200 { get; set; }
    public string? P201 { get; set; }
    public string? P202 { get; set; }
    public string? P203 { get; set; }
    public string? P204 { get; set; }
    public string? P205 { get; set; }
    public string? P206 { get; set; }
    public string? P207 { get; set; }
    public string? P208 { get; set; }
    public string? P209 { get; set; }
    public string? P210 { get; set; }
    public string? P211 { get; set; }
    public string? P212 { get; set; }
    public string? P213 { get; set; }
    public string? P214 { get; set; }
    public string? P215 { get; set; }
    public string? P216 { get; set; }
    public string? P217 { get; set; }
    public string? P218 { get; set; }
    public string? P219 { get; set; }
    public string? P220 { get; set; }
    public string? P221 { get; set; }
    public string? P222 { get; set; }
    public string? P223 { get; set; }
    public string? P224 { get; set; }
    public string? P225 { get; set; }
    public string? P226 { get; set; }
    public string? P227 { get; set; }
    public string? P228 { get; set; }
    public string? P229 { get; set; }
    public string? P230 { get; set; }
    public string? P231 { get; set; }
    public string? P232 { get; set; }
    public string? P233 { get; set; }
    public string? P234 { get; set; }
    public string? P235 { get; set; }
    public string? P236 { get; set; }
    public string? P237 { get; set; }
    public string? P238 { get; set; }
    public string? P239 { get; set; }
    public string? P240 { get; set; }
    public string? P241 { get; set; }
    public string? P242 { get; set; }
    public string? P243 { get; set; }
    public string? P244 { get; set; }
    public string? P245 { get; set; }
    public string? P246 { get; set; }
    public string? P247 { get; set; }
    public string? P248 { get; set; }
    public string? P249 { get; set; }
    public string? P250 { get; set; }
    public string? P251 { get; set; }
    public string? P252 { get; set; }
    public string? P253 { get; set; }
    public string? P254 { get; set; }
}

internal sealed class RepeatedRoot
{
    public RepeatedCell? P00 { get; set; }
    public RepeatedCell? P01 { get; set; }
    public RepeatedCell? P02 { get; set; }
    public RepeatedCell? P03 { get; set; }
    public RepeatedCell? P04 { get; set; }
    public RepeatedCell? P05 { get; set; }
    public RepeatedCell? P06 { get; set; }
    public RepeatedCell? P07 { get; set; }
    public RepeatedCell? P08 { get; set; }
    public RepeatedCell? P09 { get; set; }
    public RepeatedCell? P10 { get; set; }
    public RepeatedCell? P11 { get; set; }
    public RepeatedCell? P12 { get; set; }
    public RepeatedCell? P13 { get; set; }
    public RepeatedCell? P14 { get; set; }
    public RepeatedCell? P15 { get; set; }
    public RepeatedCell? P16 { get; set; }
    public RepeatedCell? P17 { get; set; }
    public RepeatedCell? P18 { get; set; }
    public RepeatedCell? P19 { get; set; }
    public RepeatedCell? P20 { get; set; }
    public RepeatedCell? P21 { get; set; }
    public RepeatedCell? P22 { get; set; }
    public RepeatedCell? P23 { get; set; }
    public RepeatedCell? P24 { get; set; }
    public RepeatedCell? P25 { get; set; }
    public RepeatedCell? P26 { get; set; }
    public RepeatedCell? P27 { get; set; }
    public RepeatedCell? P28 { get; set; }
    public RepeatedCell? P29 { get; set; }
    public RepeatedCell? P30 { get; set; }
    public RepeatedCell? P31 { get; set; }
}
internal sealed class RepeatedCell
{
    public string? P00 { get; set; }
    public string? P01 { get; set; }
    public string? P02 { get; set; }
    public string? P03 { get; set; }
    public string? P04 { get; set; }
    public string? P05 { get; set; }
    public string? P06 { get; set; }
    public string? P07 { get; set; }
    public string? P08 { get; set; }
    public string? P09 { get; set; }
    public string? P10 { get; set; }
    public string? P11 { get; set; }
    public string? P12 { get; set; }
    public string? P13 { get; set; }
    public string? P14 { get; set; }
    public string? P15 { get; set; }
    public string? P16 { get; set; }
    public string? P17 { get; set; }
    public string? P18 { get; set; }
    public string? P19 { get; set; }
    public string? P20 { get; set; }
    public string? P21 { get; set; }
    public string? P22 { get; set; }
    public string? P23 { get; set; }
    public string? P24 { get; set; }
    public string? P25 { get; set; }
    public string? P26 { get; set; }
    public string? P27 { get; set; }
    public string? P28 { get; set; }
    public string? P29 { get; set; }
    public string? P30 { get; set; }
    public string? P31 { get; set; }
}
