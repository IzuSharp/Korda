using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Query;

[DbTable("rcaa_items", DescriptorName = "RcaaItems")]
internal sealed record RcaaItem([property: DbKey] long Id);

internal sealed class SentinelException(string id) : Exception("DB_PERF_PRIVATE_SENTINEL:" + id);

internal static class Program
{
    private const int Rows = 4096, BatchRows = 32;
    private static readonly SentinelException PreflightSentinel = new("runtime-preflight");
    private static readonly SentinelException FluentSentinel = new("fluent-refill");
    private static readonly SentinelException OrdinarySentinel = new("ordinary-async-body");
    private static readonly AsyncLocal<string?> Marker = new();
    private static int _preflightHandlers, _fluentHandlers, _unexpectedHandlers;
    private static string? _preflightError, _fluentError, _unexpectedError;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("connection, result path, package path, side are required");
        var report = new Report(args[3], args[2]);
        DataSource? source = null;
        DataSession? session = null;
        PreparedQuery<long>? prepared = null;
        IAsyncEnumerator<long>? reader = null;
        NativeDiagnostics? native = null;
        try
        {
            if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Native AOT execution is required");
            ExceptionHandling.SetUnhandledExceptionHandler(HandleSentinel);
            ThreadPool.UnsafeQueueUserWorkItem(static (object? _) => throw PreflightSentinel, null);
            report.PreflightObserved = await WaitFor(() => Volatile.Read(ref _preflightHandlers) != 0, 3000);
            if (!report.PreflightObserved || Volatile.Read(ref _preflightHandlers) != 1)
            {
                report.Status = "inconclusive_runtime_handler";
                return Finish(report, args[1]);
            }
            var builder = new DataSourceBuilder().WithName("DB-Perf-01-private-retired-inline")
                .ConfigureLimits(new DatabaseLimits
                {
                    MaxConnections = 2, MaxWaiters = 4, MaxBatchRows = BatchRows,
                    MaxBatchBytes = 1024 * 1024, MaxFieldBytes = 4096,
                    MaxBufferedRows = Rows, MaxBufferedBytes = 16 * 1024 * 1024,
                    AdmissionTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(5),
                });
            builder.UsePostgreSql(args[0], profile => profile.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
            source = builder.Build();
            native = new NativeDiagnostics();
            report.LoadedNativePath = native.Path;
            report.LoadedNativeSha256 = native.Sha256;
            report.BeforeControl = native.Read();

            try { await OrdinaryAsyncConsumer(source); }
            catch (Exception error) when (ReferenceEquals(error, OrdinarySentinel))
            { report.OrdinaryExceptionObserved = true; report.OrdinaryException = error.ToString(); }
            await using (var check = await source.OpenSessionAsync()) report.AdmissionAfterOrdinary = true;
            report.AfterControl = native.Read();
            if (!report.OrdinaryExceptionObserved || Volatile.Read(ref _fluentHandlers) != 0)
                throw new InvalidOperationException("Ordinary async-body exception control failed");

            session = await source.OpenSessionAsync();
            using (var query = session.From(RcaaItems.Table))
            {
                query.OrderBy(RcaaItems.Id).Take(Rows);
                prepared = query.Select(query.Column(RcaaItems.Id)).Prepare();
            }
            reader = prepared.AsAsyncEnumerable(session).GetAsyncEnumerator();
            Marker.Value = "public-fluent-context";
            var callback = new CallbackState(reader);
            report.Callback = callback;
            var action = new Action(callback.Invoke);
            long read = 0;
            while (read < Rows)
            {
                var move = reader.MoveNextAsync();
                var pending = !move.IsCompleted;
                var successAtInspection = move.IsCompletedSuccessfully;
                var selected = read >= BatchRows && read % BatchRows == 0 && pending;
                report.Moves[report.MoveCount++] = new MoveReading(read, pending, successAtInspection, selected);
                if (selected)
                {
                    // No allocation, logging, hashing, or asynchronous hop between pending inspection
                    // and registration. This ValueTask is consumed only by the callback below.
                    callback.Expected = read + 1;
                    callback.Awaiter = move.GetAwaiter();
                    callback.Awaiter.UnsafeOnCompleted(action);
                    report.SelectedRowsBefore = read;
                    report.SelectedPendingRefill = true;
                    if (!await WaitFor(() => Volatile.Read(ref callback.Done) != 0, 5000))
                        throw new TimeoutException("Selected public continuation did not signal within five seconds");
                    // A fixed finite observation window lets a queued handler or the old engine catch retire.
                    // Absence of a handler is recorded, never manufactured into a timeout failure.
                    await WaitFor(() => Volatile.Read(ref _fluentHandlers) != 0, 1500);
                    break;
                }
                if (!await move) throw new InvalidOperationException("Fixed source ended before its expected row count");
                read++;
                if (reader.Current != read) throw new InvalidOperationException("Ordered primitive row did not match fixture");
            }
            report.RowsReadNormally = read;
            report.BeforeAdmission = native.Read();
            if (report.SelectedPendingRefill)
            {
                try
                {
                    await using var check = await source.OpenSessionAsync();
                    report.AdmissionAfterCallback = true;
                }
                catch (Exception error)
                {
                    report.AdmissionAfterCallback = false;
                    report.AdmissionException = error.ToString();
                }
                report.AfterAdmission = native.Read();
                report.Status = callback.Error is null && callback.Calls == 1 && callback.GetResults == 1
                    ? "observed" : "failed_callback_contract";
            }
            else report.Status = "inconclusive_no_pending_refill";
        }
        catch (Exception error)
        {
            report.PrimaryError = error.ToString();
            report.Status = "failed";
        }
        finally
        {
            if (reader is not null) await Cleanup("enumerator", reader.DisposeAsync, report);
            if (prepared is not null)
            {
                try { prepared.Dispose(); }
                catch (Exception error) { report.CleanupErrors.Add("prepared: " + error); }
            }
            if (session is not null)
            {
                await Cleanup("session", session.DisposeAsync, report);
                report.SessionLocalResourcesReleased = session.Cleanup.LocalResourcesReleased;
                report.SessionCleanup = session.Cleanup.ToString();
            }
            if (native is not null) report.AfterReaderAndSessionClose = native.Read();
            if (source is not null)
            {
                await Cleanup("source", source.DisposeAsync, report);
                report.SourceLocalResourcesReleased = source.Cleanup.LocalResourcesReleased;
                report.SourceCleanup = source.Cleanup.ToString();
            }
            if (native is not null)
            {
                report.AfterSourceClose = native.Read();
                native.Dispose();
            }
        }
        return Finish(report, args[1]);
    }

    private static async Task OrdinaryAsyncConsumer(DataSource source)
    {
        await using var session = await source.OpenSessionAsync();
        using var query = session.From(RcaaItems.Table);
        query.OrderBy(RcaaItems.Id).Take(2);
        using var prepared = query.Select(query.Column(RcaaItems.Id)).Prepare();
        await using var reader = prepared.AsAsyncEnumerable(session).GetAsyncEnumerator();
        if (!await reader.MoveNextAsync() || reader.Current != 1)
            throw new InvalidOperationException("Ordinary async control fixture mismatch");
        throw OrdinarySentinel;
    }

    private static bool HandleSentinel(Exception error)
    {
        if (ReferenceEquals(error, PreflightSentinel))
        {
            _preflightError = error.ToString();
            Interlocked.Increment(ref _preflightHandlers);
            return true;
        }
        if (ReferenceEquals(error, FluentSentinel))
        {
            _fluentError = error.ToString();
            Interlocked.Increment(ref _fluentHandlers);
            return true;
        }
        _unexpectedError = error.ToString();
        Interlocked.Increment(ref _unexpectedHandlers);
        return false;
    }

    private static async Task<bool> WaitFor(Func<bool> predicate, int milliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= milliseconds) return false;
            await Task.Delay(10);
        }
        return true;
    }

    private static async Task Cleanup(string name, Func<ValueTask> action, Report report)
    {
        try { await action(); }
        catch (Exception error) { report.CleanupErrors.Add(name + ": " + error); }
    }

    private static int Finish(Report report, string path)
    {
        report.PreflightHandlerCalls = Volatile.Read(ref _preflightHandlers);
        report.FluentHandlerCalls = Volatile.Read(ref _fluentHandlers);
        report.UnexpectedHandlerCalls = Volatile.Read(ref _unexpectedHandlers);
        report.PreflightException = _preflightError;
        report.FluentException = _fluentError;
        report.FluentSentinelAfterObservation = FluentSentinel.ToString();
        report.UnexpectedException = _unexpectedError;
        report.FinishedUtc = DateTimeOffset.UtcNow.ToString("O");
        using (var file = File.Create(path))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true })) report.Write(writer);
        Console.WriteLine("DB_PERF_PRIVATE_COMPLETION " + report.Status);
        return report.Status == "observed" && report.CleanupErrors.Count == 0 ? 0
            : report.Status.StartsWith("inconclusive", StringComparison.Ordinal) ? 3 : 2;
    }

    internal sealed class CallbackState(IAsyncEnumerator<long> reader)
    {
        internal ValueTaskAwaiter<bool> Awaiter;
        internal long Expected, Actual;
        internal int Calls, GetResults, Done, ThreadId;
        internal bool Returned, ThreadPoolThread;
        internal string? Stack, Error, Context, SynchronizationContextType;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Invoke()
        {
            Interlocked.Increment(ref Calls);
            try
            {
                Interlocked.Increment(ref GetResults);
                Returned = Awaiter.GetResult();
                Actual = reader.Current;
                if (!Returned || Actual != Expected) throw new InvalidOperationException("Selected callback row mismatch");
                ThreadId = Environment.CurrentManagedThreadId;
                ThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                Context = Marker.Value;
                SynchronizationContextType = SynchronizationContext.Current?.GetType().FullName;
                Stack = Environment.StackTrace;
            }
            catch (Exception error)
            {
                Error = error.ToString();
                Volatile.Write(ref Done, 1);
                throw;
            }
            Volatile.Write(ref Done, 1);
            throw FluentSentinel;
        }
    }
}

internal readonly record struct MoveReading(long RowsBefore, bool InitiallyPending, bool InitiallySuccessful, bool Selected);

internal sealed class Report(string side, string package)
{
    internal readonly string Side = side, Package = package, StartedUtc = DateTimeOffset.UtcNow.ToString("O");
    internal readonly string Executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
    internal readonly MoveReading[] Moves = new MoveReading[4096];
    internal readonly List<string> CleanupErrors = [];
    internal string Status = "started", FinishedUtc = "";
    internal string? LoadedNativePath, LoadedNativeSha256, OrdinaryException, AdmissionException, PrimaryError;
    internal string? PreflightException, FluentException, UnexpectedException, SessionCleanup, SourceCleanup, FluentSentinelAfterObservation;
    internal bool PreflightObserved, OrdinaryExceptionObserved, AdmissionAfterOrdinary, SelectedPendingRefill;
    internal bool? AdmissionAfterCallback, SessionLocalResourcesReleased, SourceLocalResourcesReleased;
    internal int PreflightHandlerCalls, FluentHandlerCalls, UnexpectedHandlerCalls, MoveCount;
    internal long RowsReadNormally, SelectedRowsBefore;
    internal Program.CallbackState? Callback;
    internal NativeCounts? BeforeControl, AfterControl, BeforeAdmission, AfterAdmission, AfterReaderAndSessionClose, AfterSourceClose;

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("schema_version", 1); w.WriteString("scope", "private two-process correctness gate; no performance or long-run credit");
        w.WriteString("side", Side); w.WriteString("status", Status); w.WriteString("started_utc", StartedUtc); w.WriteString("finished_utc", FinishedUtc);
        w.WriteBoolean("native_aot", !RuntimeFeature.IsDynamicCodeSupported); w.WriteString("framework_description", RuntimeInformation.FrameworkDescription);
        w.WriteString("executable", Executable); w.WriteString("executable_sha256", Hash(Executable));
        w.WriteString("package", Package); w.WriteString("package_sha256", Hash(Package));
        w.WriteString("loaded_native_path", LoadedNativePath); w.WriteString("loaded_native_sha256", LoadedNativeSha256);
        w.WriteNumber("fixture_rows", 4096); w.WriteNumber("batch_rows", 32); w.WriteNumber("max_connections", 2);
        w.WriteBoolean("preflight_observed", PreflightObserved); w.WriteNumber("preflight_handler_calls", PreflightHandlerCalls);
        w.WriteString("fluent_sentinel_after_observation", FluentSentinelAfterObservation);
        w.WriteNumber("fluent_handler_calls", FluentHandlerCalls); w.WriteNumber("unexpected_handler_calls", UnexpectedHandlerCalls);
        w.WriteString("preflight_exception", PreflightException); w.WriteString("fluent_exception", FluentException); w.WriteString("unexpected_exception", UnexpectedException);
        w.WriteBoolean("ordinary_exception_observed", OrdinaryExceptionObserved); w.WriteString("ordinary_exception", OrdinaryException);
        w.WriteBoolean("admission_after_ordinary", AdmissionAfterOrdinary); w.WriteBoolean("selected_pending_refill", SelectedPendingRefill);
        w.WriteNumber("rows_read_normally", RowsReadNormally); w.WriteNumber("selected_rows_before", SelectedRowsBefore);
        WriteNullable(w, "admission_after_callback", AdmissionAfterCallback); w.WriteString("admission_exception", AdmissionException);
        WriteNullable(w, "session_local_resources_released", SessionLocalResourcesReleased);
        WriteNullable(w, "source_local_resources_released", SourceLocalResourcesReleased);
        w.WriteString("session_cleanup", SessionCleanup); w.WriteString("source_cleanup", SourceCleanup); w.WriteString("primary_error", PrimaryError);
        w.WriteStartArray("cleanup_errors"); foreach (var e in CleanupErrors) w.WriteStringValue(e); w.WriteEndArray();
        w.WriteStartArray("moves");
        for (var i = 0; i < MoveCount; i++)
        {
            var m = Moves[i]; w.WriteStartObject(); w.WriteNumber("rows_before", m.RowsBefore); w.WriteBoolean("initially_pending", m.InitiallyPending);
            w.WriteBoolean("initially_successful", m.InitiallySuccessful); w.WriteBoolean("selected", m.Selected); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WritePropertyName("callback");
        if (Callback is { } c)
        {
            w.WriteStartObject(); w.WriteNumber("calls", c.Calls); w.WriteNumber("get_result_calls", c.GetResults);
            w.WriteBoolean("returned", c.Returned); w.WriteNumber("expected", c.Expected); w.WriteNumber("actual", c.Actual);
            w.WriteNumber("managed_thread_id", c.ThreadId); w.WriteBoolean("thread_pool_thread", c.ThreadPoolThread);
            w.WriteString("async_local", c.Context); w.WriteString("synchronization_context", c.SynchronizationContextType);
            w.WriteString("stack", c.Stack); w.WriteString("error", c.Error); w.WriteEndObject();
        }
        else w.WriteNullValue();
        Native(w, "before_control", BeforeControl); Native(w, "after_control", AfterControl);
        Native(w, "before_admission", BeforeAdmission); Native(w, "after_admission", AfterAdmission);
        Native(w, "after_reader_and_session_close", AfterReaderAndSessionClose); Native(w, "after_source_close", AfterSourceClose);
        w.WriteEndObject();
    }
    private static void WriteNullable(Utf8JsonWriter w, string name, bool? value)
    { if (value is { } b) w.WriteBoolean(name, b); else w.WriteNull(name); }
    private static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
    private static void Native(Utf8JsonWriter w, string name, NativeCounts? count)
    {
        w.WritePropertyName(name);
        if (count is not { } c) { w.WriteNullValue(); return; }
        w.WriteStartObject(); w.WriteNumber("sources", c.Sources); w.WriteNumber("sessions", c.Sessions); w.WriteNumber("operations", c.Operations);
        w.WriteNumber("cursors", c.Cursors); w.WriteNumber("buffers", c.Buffers); w.WriteNumber("owners", c.Owners);
        w.WriteNumber("sql_executions", c.SqlExecutions); w.WriteNumber("protocol_submissions", c.ProtocolSubmissions); w.WriteEndObject();
    }
}

internal sealed class NativeDiagnostics : IDisposable
{
    private readonly nint _module, _count;
    internal string Path { get; }
    internal string Sha256 { get; }
    internal NativeDiagnostics()
    {
        var paths = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == "libnw_database_postgresql.so")
            .Select(parts => parts[5]).Distinct().ToArray();
        if (paths.Length != 1) throw new InvalidOperationException("Expected exactly one actually loaded provider library");
        Path = paths[0];
        using (var f = File.OpenRead(Path)) Sha256 = Convert.ToHexStringLower(SHA256.HashData(f));
        _module = NativeLibrary.Load(Path); _count = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1");
    }
    internal unsafe NativeCounts Read()
    {
        var c = (delegate* unmanaged[Cdecl]<uint, ulong>)_count;
        return new(c(0), c(1), c(2), c(3), c(4), c(5), c(6), c(7));
    }
    public void Dispose() => NativeLibrary.Free(_module);
}
internal sealed record NativeCounts(ulong Sources, ulong Sessions, ulong Operations, ulong Cursors,
    ulong Buffers, ulong Owners, ulong SqlExecutions, ulong ProtocolSubmissions);
