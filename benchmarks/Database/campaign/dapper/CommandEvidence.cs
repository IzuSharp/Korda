using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DbPerf.Campaign;

namespace DbPerf.IndependentOrm;

internal static class CommandEvidence
{
    internal static void Write(Config config, AdapterInfo adapter)
    {
        var imagePath = Environment.ProcessPath ?? throw new InvalidOperationException("Process image path is unavailable.");
        var imageHash = Hash(imagePath);
        if (Hash(config.Manifest) != config.ParsedContractSha256)
            throw new InvalidOperationException("The shared contract changed after config parsing.");
        var proofPath = Path.Combine(AppContext.BaseDirectory, "dapper-interceptors.json");
        using var proof = JsonDocument.Parse(File.ReadAllBytes(proofPath));
        if (proof.RootElement.GetProperty("status").GetString() != "pass" ||
            proof.RootElement.GetProperty("provider").GetString() != config.Provider ||
            proof.RootElement.GetProperty("binary").GetProperty("sha256").GetString() != imageHash)
            throw new InvalidOperationException("The generated-interceptor build evidence does not match this provider/executable.");

        var path = Path.GetFullPath(config.Output + ".commands.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString("route", "dapper-aot");
        writer.WriteString("provider", config.Provider);
        writer.WriteString("captured_utc", DateTimeOffset.UtcNow);
        writer.WriteString("runtime", RuntimeInformation.FrameworkDescription);
        writer.WriteString("process_architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteBoolean("dynamic_code_supported", RuntimeFeature.IsDynamicCodeSupported);
        writer.WriteBoolean("dynamic_code_compiled", RuntimeFeature.IsDynamicCodeCompiled);
        writer.WriteString("executable_path", imagePath);
        writer.WriteString("executable_sha256", imageHash);
        writer.WriteString("manifest_path", config.Manifest);
        writer.WriteString("manifest_sha256", config.ParsedContractSha256);
        writer.WriteString("interceptors_proof_path", proofPath);
        writer.WriteString("interceptors_proof_sha256", Hash(proofPath));
        writer.WritePropertyName("interceptors_proof");
        proof.RootElement.WriteTo(writer);
        writer.WriteString("mode", config.Mode);
        writer.WriteString("session_mode", config.SessionMode);
        writer.WriteString("preparation_policy", adapter.PreparationPolicy);
        writer.WriteString("session_policy", adapter.SessionPolicy);
        writer.WriteNumber("connection_timeout_seconds", 5);
        writer.WriteNumber("command_timeout_seconds", 5);
        writer.WriteBoolean("provider_connection_pool_enabled", false);
        writer.WriteBoolean("adapter_physical_pool_enabled", config.SessionMode == "per-operation");
        writer.WriteNumber("physical_connection_capacity", config.Concurrency);
        writer.WriteBoolean("explicit_physical_prepare", false);
        writer.WriteBoolean("dapper_cache_command_enabled", config.Mode == "prepared");
        writer.WriteString("parameter_binding_policy", config.Mode == "prepared"
            ? "The generated CacheCommand factory updates parameter values on every reuse; immutable application argument objects do not skip provider rebinding."
            : "A fresh generated DbCommand binds every parameter for every execution.");
        writer.WriteString("command_cache_structure", "Prepared only: 36 public CacheCommand call-site instances, each with ThreadStatic command storage and a soft-capacity-16 shared queue; queued races and live callback threads are separate bounds. Dynamic sites do not cache commands.");
        writer.WriteString("command_cache_bound", "At most N concurrent returns imply shared-queue count <=15+N per call-site; ThreadStatic slots additionally depend on participating live threads. N alone does not bound all retained commands by 16+N.");
        writer.WriteString("parameter_rewrite_boundary", "setup: positional SQL to named @pN clauses; dynamic mode concatenates those clauses and constructs typed arguments per operation");
        writer.WriteString("allocation_scope", "End-to-end process allocation includes ORM, provider, DTOs/strings/typed JSON, relation lists, adapter, and runner. This manifest does not attribute framework-only bytes.");
        writer.WriteString("protocol_submission_count", "unmeasured; one read SQL command is not asserted to equal one protocol submission or physical round trip");
        writer.WriteString("sqlite_physical_prepare_policy", "No explicit ADO Prepare call and no hand-written ADO query fallback. CacheCommand reuse alone is not claimed to preserve sqlite3 statements across provider connection detachment.");
        writer.WriteString("wire_sql_observation", "Frozen engine SQL and named provider command SQL are retained; provider rewrite is not independently captured from the network by this adapter.");
#if SQLITE
        var native = SqliteImage.EnsureLoaded();
        writer.WriteStartObject("sqlite_image");
        writer.WriteString("path", native.Path);
        writer.WriteString("sha256", native.Sha256);
        writer.WriteString("version", native.Version);
        writer.WriteString("source_id", native.SourceId);
        writer.WriteStartArray("compile_options");
        foreach (var option in native.CompileOptions) writer.WriteStringValue(option);
        writer.WriteEndArray();
        writer.WriteString("comparison_status", "Require equality against the native and Korda engine fingerprints before qualifying performance results.");
        writer.WriteEndObject();
#else
        writer.WriteString("database_driver", "Npgsql 11.0.0 managed PostgreSQL protocol implementation; no libpq in this route");
        writer.WriteNumber("npgsql_max_auto_prepare", 0);
#endif
        writer.WriteStartArray("selected_cases");
        foreach (var selected in config.Mix) writer.WriteStringValue(selected.Id);
        writer.WriteEndArray();
        writer.WriteStartArray("cases");
        foreach (var query in config.Cases)
        {
            writer.WriteStartObject();
            writer.WriteString("id", query.Id);
            writer.WriteString("name", query.Name);
            if (query.Steps.Length != 0)
            {
                WriteStateCase(writer, query, config);
                writer.WriteEndObject();
                continue;
            }
            var plan = new ReadPlan(query, config.Provider, config.Mode == "prepared");
            writer.WriteString("frozen_engine_sql", query.Sql);
            writer.WriteString("provider_command_sql", plan.CommandSql);
            writer.WriteStartArray("provider_command_fragments");
            foreach (var clause in plan.CommandFragments) writer.WriteStringValue(clause);
            writer.WriteEndArray();
            writer.WriteString("materialization", query.Materialization);
#if SQLITE
            if (query.Materialization == "nullable-money")
                writer.WriteString("scalar_conversion", "Dapper.AOT Int64? INTEGER cents -> decimal? business value; its built-in decimal scalar factory bypasses the declared custom handler");
#endif
            writer.WriteString("parameter_shape", query.Materialization == "related"
                ? nameof(RelatedParameters96) : plan.HasText ? nameof(LiteralTypedArgs) : nameof(CommonTypedArgs));
            writer.WriteBoolean("arguments_reused", config.Mode == "prepared");
            writer.WriteNumber("logical_read_sql_commands", 1);
            writer.WriteStartArray("ordered_parameters");
            for (var index = 0; index < query.Parameters.Length; index++)
            {
                var parameter = query.Parameters[index];
                var ordinal = (index + 1).ToString(CultureInfo.InvariantCulture);
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", index + 1);
                writer.WriteString("source_token", config.Provider == "sqlite" ? "?" : "$" + ordinal);
                writer.WriteString("command_token", "@p" + ordinal);
                writer.WriteString("parameter_name", "p" + ordinal);
                writer.WriteString("type", parameter.Type);
                if (parameter.Type is "int64" or "int32") writer.WriteNumber("value", parameter.Integer);
                else writer.WriteString("value", parameter.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("columns");
            foreach (var column in query.Columns)
            {
                writer.WriteStartObject();
                writer.WriteString("name", column.Name);
                writer.WriteString("type", column.Type);
                writer.WriteBoolean("nullable", column.Nullable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("expected_rows", query.Expected.Rows);
            writer.WriteString("expected_digest", query.Expected.Digest.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStateCase(Utf8JsonWriter writer, Case query, Config config)
    {
        var plan = new StatePlan(query, config.Provider, config.Mode == "prepared", 0);
        writer.WriteString("materialization", "state");
        writer.WriteString("state_row_mapping", "Dapper.AOT StateRow(Id, Counter, Version, Note); Id independently checked; digest consumes only Counter, Version, Note for each SELECT");
        writer.WriteString("transaction_control", config.Provider == "sqlite"
            ? "ADO.NET BeginTransaction(deferred:true), CommitAsync, RollbackAsync; provider may complete synchronously"
            : "ADO.NET BeginTransactionAsync(ReadCommitted), CommitAsync, RollbackAsync; physical protocol batching is not inferred from API calls");
        writer.WriteBoolean("arguments_reused", config.Mode == "prepared");
        writer.WriteNumber("expected_rows", query.Expected.Rows);
        writer.WriteString("expected_digest", query.Expected.Digest.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("expected_outcome", query.Expected.Outcome);
        writer.WriteStartArray("steps");
        foreach (var step in plan.Steps)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", step.Contract.Kind);
            writer.WriteString("frozen_engine_sql", step.Contract.Sql);
            writer.WriteString("provider_command_sql", step.CommandSql);
            writer.WriteString("parameter_shape", step.Shape);
            writer.WriteStartArray("provider_command_fragments");
            foreach (var clause in step.CommandFragments) writer.WriteStringValue(clause);
            writer.WriteEndArray();
            writer.WriteStartArray("ordered_parameters");
            for (var index = 0; index < step.Contract.Parameters.Length; index++)
            {
                var parameter = step.Contract.Parameters[index];
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", index + 1);
                writer.WriteString("command_token", "@p" + (index + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteString("type", parameter.Type);
                if (parameter.Runtime is null)
                {
                    if (parameter.Type is "int64" or "int32") writer.WriteNumber("value", parameter.Integer);
                    else writer.WriteString("value", parameter.Text);
                }
                else
                {
                    writer.WriteString("runtime", parameter.Runtime);
                    writer.WriteStartArray("actual_values_by_worker_zero_based");
                    for (var worker = 0; worker < config.Concurrency; worker++) writer.WriteNumberValue(worker + 1L);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (step.Contract.ExpectedAffected is { } affected) writer.WriteNumber("expected_affected", affected);
            if (step.Contract.ExpectedCounter is { } counter)
            {
                writer.WriteStartObject("expected_state");
                writer.WriteNumber("counter", counter);
                writer.WriteNumber("version", step.Contract.ExpectedVersion!.Value);
                writer.WriteString("note", step.Contract.ExpectedNote);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
