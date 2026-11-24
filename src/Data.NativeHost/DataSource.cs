using Korda.Data;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Lifetime;
using Korda.Query.Internal;

namespace Korda.Database;

/// <summary>Owns one lazy C++ connection pool, bounded runtime and source-generation metadata registry.</summary>
/// <remarks>Sources support concurrent independent session opens. Closing rejects new work and retains every
/// admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.</remarks>
public sealed partial class DataSource : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new(), _registrations = new();
    private readonly SourceResources _resources;
    private readonly HostAsyncCalls _async;
    private readonly Dictionary<CodecDescriptor, uint> _codecs = [];
    private readonly Dictionary<string, DatabaseOwner> _models = new(StringComparer.Ordinal);
    private readonly CloseController _close;
    private bool _closing, _poisoned;
    internal SourceBinding Binding { get; }
    internal DatabaseOwner Owner { get; }
    internal Action<AsyncBoundary>? TestBoundary { get; set; }
    internal Action? TestAfterErrorTake { get; set; }
    internal Action? TestAfterOwnerTake { get; set; }
    internal Action<AsyncSnapshot>? TestRetirement { get; set; }
    private DataSource(SourceBinding binding, DatabaseOwner owner, DatabaseProvider provider, string name, DatabaseLimits limits, EngineInfo info)
    {
        Binding = binding; Owner = owner; Provider = provider; Name = name; Limits = limits; Capabilities = new(provider, info);
        _resources = new(owner, checked(limits.MaxQueryNodes + limits.MaxConnections + limits.MaxWaiters + 2));
        _async = new(this); _close = new(this, owner, FinishClose);
    }
    internal static unsafe DataSource Create(EngineDescriptor descriptor, DatabaseProvider provider, string name, DatabaseLimits limits, byte[] config)
    {
        var info = descriptor.Inspect();
        var calls = new NativeCalls(descriptor.EngineId, symbol => EngineModuleLoader.ResolveExport(descriptor, symbol));
        var binding = new SourceBinding(calls, provider == DatabaseProvider.Sqlite ? 1u : 2u);
        BindingChecks.Require((nint)calls.nw_db_source_create_v1, "source_create");
        nint pointer = 0; var error = BindingChecks.Error(); fixed (byte* bytes = config)
            HostFailure.Check(binding, calls.nw_db_source_create_v1(BindingChecks.View(bytes, config.Length), &pointer, &error), error);
        DatabaseOwner? owner = null;
        try { owner = new(binding, DatabaseOwnerKind.Source, pointer); return new(binding, owner, provider, name, limits, info); }
        catch { if (owner is not null) owner.Dispose(); else calls.nw_db_owner_release_v1(pointer); throw; }
    }
    /// <summary>Gets the immutable noncredential diagnostic name.</summary>
    public string Name { get; }
    /// <summary>Gets the selected native provider.</summary>
    public DatabaseProvider Provider { get; }
    /// <summary>Gets the actual negotiated provider capability snapshot.</summary>
    public DatabaseCapabilities Capabilities { get; }
    /// <summary>Gets the immutable source limits.</summary>
    public DatabaseLimits Limits { get; }
    /// <summary>Gets the current cleanup observation, retained after disposal.</summary>
    public DatabaseCloseResult Cleanup => _close.Snapshot;
    internal void CheckOpen()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_poisoned) throw new NativeEngineException("The database engine has closed admission after a bridge failure.");
        }
    }
    internal void Poison() { lock (_gate) _poisoned = true; }
    internal Task<T> Start<T>(Func<nint> create, Func<nint, T> take, CancellationToken token = default, bool control = false)
    {
        if (!control) { CheckOpen(); if (token.IsCancellationRequested) return Task.FromCanceled<T>(token); }
        OperationHandle? handle = null; NativeResourceOwner? resource = null;
        try
        {
            handle = new(Binding, create()); resource = new(handle, this);
            var operation = new AsyncOperation<T>(resource, _async, _resources.Coordinator, take, token, testBoundary: TestBoundary);
            return Complete(operation, operation.Start());
        }
        catch { if (resource is not null) resource.Dispose(); else handle?.Dispose(); throw; }
    }
    // Only the internal single-consumer fetch seam uses this result form. Public
    // Task APIs keep their independently awaitable result and cleanup bridge.
    internal ValueTask<T> StartRetiredValueTask<T>(Func<nint> create, Func<nint, T> take, CancellationToken token)
    {
        CheckOpen(); if (token.IsCancellationRequested) return new(Task.FromCanceled<T>(token));
        OperationHandle? handle = null;
        try
        {
            handle = new(Binding, create());
            var operation = new AsyncOperation.RetiredValueTask<T>(handle, _async, _resources.Coordinator, take, token,
                testBoundary: TestBoundary);
            handle = null;
            return operation.Start();
        }
        catch { handle?.Dispose(); throw; }
    }
    private async Task<T> Complete<T>(AsyncOperation<T> operation, Task<T> result)
    {
        try { return await result.ConfigureAwait(false); }
        catch (OperationCanceledException) when (operation.TerminalFailure is OperationCanceledException { InnerException: not null })
        {
            // The Core task retains cancellation semantics; the domain preserves its
            // copied cleanup diagnostic after the native operation has retired.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operation.TerminalFailure!).Throw();
            throw;
        }
        finally { await operation.Retirement.ConfigureAwait(false); TestRetirement?.Invoke(operation.Snapshot); }
    }
    /// <summary>Acquires an independent physical session through bounded native pool admission.</summary>
    /// <param name="cancellationToken">Cancels admission or requests accepted native work to stop.</param>
    /// <returns>A session that must be disposed; its ownership is independent from this operation.</returns>
    /// <exception cref="ObjectDisposedException">The source has closed admission.</exception>
    /// <exception cref="DatabaseException">Admission or the provider fails.</exception>
    /// <exception cref="OperationCanceledException">The native operation confirms cancellation.</exception>
    public Task<DataSession> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        CheckOpen(); if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<DataSession>(cancellationToken);
        return Start(() => NativeHost.Open(this), operation =>
        {
            var owner = NativeHost.TakeOwner(Binding, operation, 1, DatabaseOwnerKind.Session);
            try { TestAfterOwnerTake?.Invoke(); return new DataSession(this, owner); } catch { owner.Dispose(); throw; }
        }, cancellationToken);
    }
    /// <summary>Opens and closes a real session, returning false only for provider connection failures.</summary>
    /// <param name="cancellationToken">The connection test cancellation token.</param>
    /// <returns>True when a native session was established; false for a provider connection failure.</returns>
    /// <exception cref="ObjectDisposedException">Admission is closed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed.</exception>
    /// <remarks>Configuration, loading, resource, panic and bridge failures retain their exception types.</remarks>
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    { CheckOpen(); if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken); return CanConnectCore(cancellationToken); }
    private async Task<bool> CanConnectCore(CancellationToken token)
    {
        try { await using var session = await OpenSessionAsync(token).ConfigureAwait(false); return true; }
        catch (DatabaseException failure) when (failure.Kind == DatabaseErrorKind.Provider) { return false; }
    }
    /// <summary>Starts closure and waits only within the caller's independent budget.</summary>
    /// <param name="waitBudget">A finite nonnegative wait; zero returns the current observation.</param>
    /// <param name="cancellationToken">Cancels this wait while native cleanup continues.</param>
    /// <returns>A truthful cleanup observation; expired waits report Pending and unreleased local resources.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget is invalid.</exception>
    /// <exception cref="OperationCanceledException">The wait is canceled after closure starts.</exception>
    public Task<DatabaseCloseResult> CloseAsync(TimeSpan waitBudget, CancellationToken cancellationToken = default)
    { HostEncoding.WaitBudget(waitBudget); RequestClose(); return _close.WaitAsync(waitBudget, cancellationToken); }
    private void RequestClose()
    {
        lock (_gate) _closing = true;
        lock (_registrations) { ReleaseRelations(); foreach (var model in _models.Values) model.Dispose(); _models.Clear(); _codecs.Clear(); }
        _close.Request();
    }
    partial void ReleaseRelations();
    private void FinishClose() { Owner.Dispose(); _resources.Dispose(); }
    /// <summary>Closes admission and requests bounded native cleanup without blocking for I/O.</summary>
    public void Dispose() => RequestClose();
    /// <summary>Closes admission and joins actual local safety, even beyond an application policy deadline.</summary>
    /// <returns>The native cleanup join.</returns>
    public ValueTask DisposeAsync() { RequestClose(); return new(_close.JoinAsync()); }
    internal unsafe uint BindCodec(CodecDescriptor codec)
    {
        CheckOpen(); lock (_registrations)
        {
            if (_codecs.TryGetValue(codec, out var existing)) return existing;
            using var lease = Owner.Borrow(DatabaseOwnerKind.Source); var bytes = codec.Configuration is { } config ? HostEncoding.Utf8.GetBytes(config) : [];
            uint result = 0; var error = BindingChecks.Error(); fixed (byte* input = bytes)
                HostFailure.Check(Binding, Binding.Calls.nw_db_codec_register_v1(lease.Pointer, codec.Id, BindingChecks.View(input, bytes.Length), &result, &error), error);
            if (result == 0) throw new NativeEngineException("The native codec binding is zero."); _codecs.Add(codec, result); return result;
        }
    }
    internal DatabaseOwner RegisterModel<TEntity>(Table<TEntity> table) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table); CheckOpen(); lock (_registrations)
        {
            if (_models.TryGetValue(table.MetadataHash, out var model)) return model;
            foreach (var column in table.Columns) BindCodec(column.Definition.Codec);
            try { model = MetadataBindings.RegisterModel(Owner, table); _models.Add(table.MetadataHash, model); return model; }
            catch (BindingFailure failure) { throw HostFailure.Binding(Binding, failure); }
        }
    }
    internal uint RegisterProjection<T>(Projection<T> projection)
    {
        ArgumentNullException.ThrowIfNull(projection); CheckOpen();
        try { return MetadataBindings.RegisterProjection(Owner, projection, BindCodec); }
        catch (BindingFailure failure) { throw HostFailure.Binding(Binding, failure); }
    }
}

internal sealed class CloseController(DataSource source, DatabaseOwner owner, Action release)
{
    private readonly object _gate = new();
    private Task<DatabaseCloseResult>? _task;
    private DatabaseCloseResult _last = new(CleanupStatus.NotStarted, 0, null, false, ConnectionDisposition.Usable, false);
    internal DatabaseCloseResult Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_last.LocalResourcesReleased || owner.IsClosed) return _last;
                var latest = NativeHost.Snapshot(owner).Cleanup;
                if (_task is not null && latest.Status == CleanupStatus.NotStarted) return _last;
                return _last = latest;
            }
        }
    }
    internal void Request()
    {
        lock (_gate)
        {
            if (_task is not null) return;
            var observed = NativeHost.Snapshot(owner).Cleanup;
            if (observed.LocalResourcesReleased) { _last = observed; _task = Task.FromResult(observed); release(); return; }
            _last = _last with { Status = CleanupStatus.Pending };
            try { _task = Finish(source.Start(() => NativeHost.Close(owner), operation => { using var buffer = NativeHost.TakeBuffer(source.Binding, operation); return buffer.ReadSnapshot().Cleanup; }, control: true)); }
            catch (Exception failure) { _task = Task.FromException<DatabaseCloseResult>(failure); _last = _last with { Status = CleanupStatus.Failed }; }
        }
    }
    private async Task<DatabaseCloseResult> Finish(Task<DatabaseCloseResult> completion)
    {
        try
        {
            var result = await completion.ConfigureAwait(false);
            lock (_gate) _last = result;
            if (result.LocalResourcesReleased) release();
            return result;
        }
        catch { lock (_gate) _last = _last with { Status = CleanupStatus.Failed }; throw; }
    }
    internal async Task<DatabaseCloseResult> WaitAsync(TimeSpan budget, CancellationToken token)
    {
        Request(); token.ThrowIfCancellationRequested(); if (budget == TimeSpan.Zero) return Snapshot;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining = budget - System.Diagnostics.Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero) return Snapshot with { Status = CleanupStatus.Pending, LocalResourcesReleased = false, DeadlineExceeded = true };
            try { return await _task!.WaitAsync(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token).ConfigureAwait(false); }
            catch (TimeoutException) { if (_task!.IsCompleted) return await _task.ConfigureAwait(false); }
        }
    }
    internal async Task JoinAsync() { Request(); var result = await _task!.ConfigureAwait(false); if (!result.LocalResourcesReleased) throw new DatabaseCleanupException("Native cleanup did not establish local safety.", result); }
}
