using System.Runtime.CompilerServices;
using DbPerf.Campaign;
using DbPerf.IndependentOrm;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
    throw new InvalidOperationException("Execute the published Native AOT image directly.");

var lifecycle = args.Contains("--lifecycle-probe", StringComparer.Ordinal);
var campaignArgs = lifecycle ? args.Where(value => value != "--lifecycle-probe").ToArray() : args;
var config = Config.Parse(campaignArgs, DapperWorker.ProviderName);
if (config.Mode == "prepared" && config.ParsedContractSha256 != PreparedSql.ContractSha256)
    throw new InvalidOperationException("Prepared ORM call sites were compiled from a different contract.");
#if SQLITE
var selectedNative = Array.IndexOf(args, "--sqlite-native");
if (selectedNative >= 0)
    SqliteImage.ExplicitPath = selectedNative + 1 < args.Length
        ? args[selectedNative + 1] : throw new ArgumentException("Missing --sqlite-native value.");
#endif
var adapter = DapperWorker.CreateInfo(config);
CommandEvidence.Write(config, adapter);
if (lifecycle) return await DapperWorker.RunLifecycleProbeAsync(config, adapter).ConfigureAwait(false);
return await Runner.RunAsync(config, worker => DapperWorker.CreateAsync(config, worker), adapter,
    DapperWorker.CollectOwnerRetirementAsync).ConfigureAwait(false);
