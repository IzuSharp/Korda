using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DbPerf.Campaign;

internal readonly record struct Parameter(string Type, long Integer, string? Text, string? Runtime = null);
internal readonly record struct Column(string Name, string Type, bool Nullable);
internal readonly record struct Result(long Rows, ulong Digest, long Suspensions = 0, string Outcome = "success");

internal sealed class Step
{
    internal required string Kind { get; init; }
    internal required string Sql { get; init; }
    internal required string[] Fragments { get; init; }
    internal required Parameter[] Parameters { get; init; }
    internal long? ExpectedAffected { get; init; }
    internal long? ExpectedCounter { get; init; }
    internal long? ExpectedVersion { get; init; }
    internal string? ExpectedNote { get; init; }
}

internal sealed class Case
{
    internal required string Id { get; init; }
    internal required string Name { get; init; }
    internal required string Sql { get; init; }
    internal required string[] Fragments { get; init; }
    internal required Parameter[] Parameters { get; init; }
    internal required Column[] Columns { get; init; }
    internal required string Materialization { get; init; }
    internal required Result Expected { get; init; }
    internal int GroupCount { get; init; }
    internal long[] ParentIds { get; init; } = [];
    internal Step[] Steps { get; init; } = [];
}

internal sealed class Config
{
    internal required string Provider { get; init; }
    internal required string Connection { get; init; }
    internal required string Manifest { get; init; }
    internal required string ParsedContractSha256 { get; init; }
    internal required string Output { get; init; }
    internal required Case[] Cases { get; init; }
    internal required Case[] Mix { get; init; }
    internal string Mode { get; init; } = "prepared";
    internal string SessionMode { get; init; } = "persistent";
    internal int Concurrency { get; init; } = 1;
    internal int Warmup { get; init; } = 64;
    internal double Seconds { get; init; } = 10;
    internal double Rate { get; init; }
    internal double? RecoveryRate { get; init; }
    internal double? RecoveryAfter { get; init; }
    internal long Operations { get; init; }
    internal bool EmptyDriver { get; init; }
    internal bool Initialize { get; init; }
    internal bool VerifyOnly { get; init; }
    internal int Tenants { get; init; }
    internal int OrdersPerTenant { get; init; }
    internal int Rows { get; init; }
    internal int BatchRows { get; init; }
    internal long Seed { get; init; } = 1289;
    internal string? ExportSql { get; init; }
    internal bool ExportOnly { get; init; }

    internal static Config Parse(string[] args, string provider)
    {
        var options = new Dictionary<string,string>(StringComparer.Ordinal);
        for (var i=0;i<args.Length;i++)
        {
            var name=args[i];
            if (!name.StartsWith("--",StringComparison.Ordinal)) throw new ArgumentException("Expected option: "+name);
            if (name is "--empty-driver" or "--initialize" or "--verify-only" or "--export-only") options.Add(name,"true");
            else options.Add(name,++i<args.Length?args[i]:throw new ArgumentException("Missing option value"));
        }
        string Get(string key,string? fallback=null)=>options.TryGetValue("--"+key,out var value)?value:fallback??throw new ArgumentException("Missing --"+key);
        int Integer(string key,int fallback)=>int.Parse(Get(key,fallback.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
        double Number(string key,double fallback)=>double.Parse(Get(key,fallback.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
        var path=Path.GetFullPath(Get("manifest"));
        var manifestBytes=File.ReadAllBytes(path);
        var parsedContractSha256=Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        using var json=JsonDocument.Parse(manifestBytes);
        var root=json.RootElement;
        if(root.GetProperty("schema_version").GetInt32()!=1) throw new InvalidOperationException("Unknown campaign contract");
        var dataset=root.GetProperty("dataset");
        var cases=new List<Case>();
        foreach(var row in root.GetProperty("cases").EnumerateArray())
        {
            var expected=row.GetProperty("expected");
            Parameter ParameterValue(JsonElement p)
            {
                var kind=p.GetProperty("type").GetString()!;
                var runtime=p.TryGetProperty("runtime",out var variable)?variable.GetString():null;
                return kind=="text"?new Parameter(kind,0,p.GetProperty("value").GetString(),runtime):new Parameter(kind,p.GetProperty("value").GetInt64(),null,runtime);
            }
            var parameters=row.GetProperty("parameters").EnumerateArray().Select(ParameterValue).ToArray();
            cases.Add(new Case
            {
                Id=row.GetProperty("id").GetString()!, Name=row.GetProperty("name").GetString()!,
                Sql=row.GetProperty("sql").GetProperty(provider).GetString()!,
                Fragments=row.GetProperty("fragments").GetProperty(provider).EnumerateArray().Select(p=>p.GetString()!).ToArray(),
                Parameters=parameters,
                Columns=row.GetProperty("columns").EnumerateArray().Select(p=>new Column(p.GetProperty("name").GetString()!,p.GetProperty("type").GetString()!,p.GetProperty("nullable").GetBoolean())).ToArray(),
                Materialization=row.GetProperty("materialization").GetString()!,
                Expected=new(expected.GetProperty("rows").GetInt64(),ulong.Parse(expected.GetProperty("digest").GetString()!,CultureInfo.InvariantCulture),0,
                    expected.TryGetProperty("outcome",out var outcome)?outcome.GetString()!:"success"),
                GroupCount=row.TryGetProperty("group_count",out var count)?count.GetInt32():0,
                ParentIds=row.TryGetProperty("parent_ids",out var parents)?parents.EnumerateArray().Select(p=>p.GetInt64()).ToArray():[],
                Steps=row.TryGetProperty("steps",out var steps)?steps.EnumerateArray().Select(step=>new Step
                {
                    Kind=step.GetProperty("kind").GetString()!,Sql=step.GetProperty("sql").GetProperty(provider).GetString()!,
                    Fragments=step.GetProperty("fragments").GetProperty(provider).EnumerateArray().Select(p=>p.GetString()!).ToArray(),
                    Parameters=step.GetProperty("parameters").EnumerateArray().Select(ParameterValue).ToArray(),
                    ExpectedAffected=step.TryGetProperty("expected_affected",out var affected)?affected.GetInt64():null,
                    ExpectedCounter=step.TryGetProperty("expected_state",out var state)?state.GetProperty("counter").GetInt64():null,
                    ExpectedVersion=step.TryGetProperty("expected_state",out state)?state.GetProperty("version").GetInt64():null,
                    ExpectedNote=step.TryGetProperty("expected_state",out state)?state.GetProperty("note").GetString():null,
                }).ToArray():[],
            });
        }
        var selected=Get("case","mixed");
        var excluded=Get("exclude","").Split(',',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var mix=cases.Where(c=>(selected=="mixed"||c.Name==selected)&&!excluded.Contains(c.Name)).ToArray();
        if(mix.Length==0) throw new ArgumentException("The selected case set is empty");
        var config=new Config
        {
            Provider=provider,Connection=Get("connection"),Manifest=path,ParsedContractSha256=parsedContractSha256,Output=Get("output"),Cases=cases.ToArray(),Mix=mix,
            Mode=Get("mode","prepared"),SessionMode=Get("session-mode","persistent"),Concurrency=Integer("concurrency",1),
            Warmup=Integer("warmup",64),Seconds=Number("seconds",10),Rate=Number("rate",0),
            RecoveryRate=options.ContainsKey("--recovery-rate")?Number("recovery-rate",0):null,
            RecoveryAfter=options.ContainsKey("--recovery-after")?Number("recovery-after",0):null,
            Operations=long.Parse(Get("operations","0"),CultureInfo.InvariantCulture),
            EmptyDriver=Get("empty-driver","false")=="true",Initialize=Get("initialize","false")=="true",VerifyOnly=Get("verify-only","false")=="true",
            Tenants=dataset.GetProperty("tenants").GetInt32(),OrdersPerTenant=dataset.GetProperty("orders_per_tenant").GetInt32(),
            Rows=dataset.GetProperty("rows").GetInt32(),BatchRows=dataset.GetProperty("batch_rows").GetInt32(),
            Seed=long.Parse(Get("seed","1289"),CultureInfo.InvariantCulture),
            ExportSql=options.GetValueOrDefault("--export-sql"),ExportOnly=Get("export-only","false")=="true",
        };
        if(config.Concurrency<1||config.Concurrency>128||config.Seconds<0||config.Rate<0||config.Operations<0||config.Warmup<0) throw new ArgumentException("Invalid finite campaign limits");
        if(config.Mode is not ("dynamic" or "prepared")||config.SessionMode is not ("persistent" or "per-operation")) throw new ArgumentException("Unknown construction/session mode");
        config.ValidateArrivalSchedule();
        return config;
    }

    internal void ValidateArrivalSchedule()
    {
        const long maximumOperations=1_000_000_000;
        if(!double.IsFinite(Seconds)||Seconds is <0 or >7200
            ||!double.IsFinite(Rate)||Rate is <0 or >1_000_000
            ||Operations is <0 or >maximumOperations)
            throw new ArgumentException("Arrival schedule limits must be finite and bounded.");
        if(Rate>0&&Operations>0)
            throw new ArgumentException("--operations and a positive --rate are incompatible.");
        if(RecoveryRate.HasValue!=RecoveryAfter.HasValue)
            throw new ArgumentException("--recovery-rate and --recovery-after must be supplied together.");
        if(RecoveryRate is { } recoveryRate&&RecoveryAfter is { } recoveryAfter)
        {
            if(Rate<=0||Operations!=0||!double.IsFinite(recoveryRate)||recoveryRate is <=0 or >1_000_000
                ||!double.IsFinite(recoveryAfter)||recoveryAfter<=0||recoveryAfter>=Seconds)
                throw new ArgumentException("Recovery requires a positive fixed rate, no --operations, and 0 < recovery-after < seconds.");
            double first=Math.Floor(Rate*recoveryAfter),second=Math.Floor(recoveryRate*(Seconds-recoveryAfter));
            if(first<1||second<1||first+second>maximumOperations)
                throw new ArgumentException("Both arrival phases need at least one planned operation, with at most 1,000,000,000 combined.");
        }
        else if(Rate>0&&Math.Floor(Rate*Seconds) is <1 or >maximumOperations)
            throw new ArgumentException("The offered schedule must contain 1..1,000,000,000 operations.");
    }
}

internal interface ICampaignWorker : IAsyncDisposable
{
    ValueTask<Result> ExecuteAsync(Case query,long sequence);
}

internal readonly record struct AdapterInfo(string Route,string Provider,string PackageVersion,
    string PreparationPolicy,string SessionPolicy,string[] LoadedNativePaths);

internal sealed class CampaignException : Exception
{
    internal string Category { get; }
    internal string? ProviderCode { get; }
    internal CampaignException(string category,string? providerCode,Exception inner)
        :base(inner.Message,inner){Category=category;ProviderCode=providerCode;}
}
