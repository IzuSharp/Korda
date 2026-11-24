using System.Globalization;

internal sealed record Options
{
    internal string Connection { get; init; } = "";
    internal string Case { get; init; } = "numeric";
    internal string Mode { get; init; } = "prepared";
    internal string SessionMode { get; init; } = "persistent";
    internal int Concurrency { get; init; } = 1;
    internal int Warmup { get; init; } = 64;
    internal int Rows { get; init; } = 32;
    internal int BatchRows { get; init; } = 32;
    internal int Tenants { get; init; } = 8;
    internal int OrdersPerTenant { get; init; } = 1024;
    internal long Operations { get; init; } = 1000;
    internal double Seconds { get; init; }
    internal bool Initialize { get; init; }
    internal bool Verify { get; init; }
    internal bool EmptyDriver { get; init; }
    internal string? Output { get; init; }
    internal string[] SelectedCases { get; init; } = [];

    internal static Options Parse(string[] args)
    {
        var p = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            int Number() => int.Parse(Value(), CultureInfo.InvariantCulture);
            p = args[i] switch
            {
                "--connection" => p with { Connection = Value() },
                "--case" => p with { Case = Value() },
                "--mode" => p with { Mode = Value() },
                "--session" => p with { SessionMode = Value() },
                "--concurrency" => p with { Concurrency = Number() },
                "--warmup" => p with { Warmup = Number() },
                "--rows" => p with { Rows = Number() },
                "--batch-rows" => p with { BatchRows = Number() },
                "--tenants" => p with { Tenants = Number() },
                "--orders-per-tenant" => p with { OrdersPerTenant = Number() },
                "--operations" => p with { Operations = long.Parse(Value(), CultureInfo.InvariantCulture), Seconds = 0 },
                "--seconds" => p with { Seconds = double.Parse(Value(), CultureInfo.InvariantCulture), Operations = 0 },
                "--initialize" => p with { Initialize = true },
                "--verify" => p with { Verify = true },
                "--empty-driver" => p with { EmptyDriver = true },
                "--output" => p with { Output = Value() },
                _ => throw new ArgumentException("Unknown option " + args[i]),
            };
        }
        if (p.Connection.Length == 0) throw new ArgumentException("--connection must identify a dedicated DB-Perf-01 database.");
        if (p.Mode is not ("dynamic" or "prepared")) throw new ArgumentException("--mode must be dynamic or prepared.");
        if (p.SessionMode is not ("persistent" or "per-operation")) throw new ArgumentException("--session must be persistent or per-operation.");
        if (p.Concurrency is < 1 or > 256 || p.Warmup is < 1 or > 100000 || p.Rows is < 1 or > 1000000
            || p.BatchRows is < 1 or > 4096 || p.Tenants is < 1 or > 1024 || p.OrdersPerTenant < 32
            || p.Seconds < 0 || p.Seconds > 86400 || p.Operations < 0 || (!p.Initialize && p.Seconds == 0 && p.Operations == 0))
            throw new ArgumentOutOfRangeException(nameof(args), "Invalid or unbounded workload size.");
        return p;
    }
}
