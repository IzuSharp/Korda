using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace NativeWeave.Server;

/// <summary>Owns one Rust Host, frozen application routing and optionally owned application services.</summary>
/// <remarks>Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
public sealed class NativeServerApplication : ServerRoutes, IAsyncDisposable
{
    private readonly ServerHost _host;
    private readonly bool _ownsServices;
    private readonly List<ServerMiddleware> _middleware = [];
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _dispose;
    private int _state = (int)ServerState.Created;
    private static readonly AsyncLocal<NativeServerApplication?> Executing = new();
    internal ServerConfiguration Configuration { get; }
    internal NativeServerApplication(ServerConfiguration configuration, ServerHost host, IServiceProvider services, bool ownsServices)
        : base(new(), new(null, string.Empty))
    { Configuration = configuration; _host = host; Services = services; _ownsServices = ownsServices; }
    /// <summary>Gets application services; request-scoped services are resolved through RequestContext.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    public IServiceProvider Services { get; }
    /// <summary>Gets the atomic lifecycle observation.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    public ServerState State => (ServerState)Volatile.Read(ref _state);
    /// <summary>Gets negotiated native capabilities.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    public ServerCapabilities Capabilities => _host.Capabilities;
    /// <summary>Gets actual listener addresses after successful startup.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    public IReadOnlyList<Uri> BoundAddresses => _host.BoundAddresses;
    /// <summary>Adds middleware before freeze; the first registered middleware is outermost.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    /// <param name="middleware">The middleware value described by this operation.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public NativeServerApplication Use(ServerMiddleware middleware)
    { Registry.Mutable(); ArgumentNullException.ThrowIfNull(middleware); _middleware.Add(middleware); return this; }
    /// <summary>Freezes all routes and starts once; success means every native listener accepts requests.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycle)
        {
            if (State != ServerState.Created) throw new InvalidOperationException("The application can start only once from Created.");
            Registry.Freeze(Configuration);
            Volatile.Write(ref _state, (int)ServerState.Starting);
            return StartCoreAsync(cancellationToken);
        }
    }
    private async Task StartCoreAsync(CancellationToken token)
    {
        await Task.Yield();
        try
        {
            await _host.StartAsync(Registry.Routes, DispatchAsync, token).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _state, (int)ServerState.Running, (int)ServerState.Starting);
        }
        catch { Interlocked.CompareExchange(ref _state, (int)ServerState.Faulted, (int)ServerState.Starting); throw; }
    }
    /// <summary>Starts and waits for shutdown, then joins all ownership; Ctrl+C/SIGTERM handlers are scoped to this call.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public Task RunAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return RunCoreAsync(cancellationToken); }
    private async Task RunCoreAsync(CancellationToken token)
    {
        Exception? failure = null;
        using var signal = CancellationTokenSource.CreateLinkedTokenSource(token);
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; signal.Cancel(); };
        PosixSignalRegistration? terminate = null;
        try
        {
            Console.CancelKeyPress += cancel;
            if (!OperatingSystem.IsWindows()) terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; signal.Cancel(); });
            await StartAsync(token).ConfigureAwait(false);
            try
            {
                Task terminal = await Task.WhenAny(_stopped.Task, _host.Lifetime).WaitAsync(signal.Token).ConfigureAwait(false);
                await terminal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (signal.IsCancellationRequested) { }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            terminate?.Dispose(); Console.CancelKeyPress -= cancel;
            try { await DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    /// <summary>Closes admission and waits a finite interval; Pending retains outstanding roots and credits.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    /// <param name="timeout">The finite checked wait or request duration specified by this operation.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public Task<ServerCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ServerValidation.Duration(timeout, true, nameof(timeout)); cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycle)
        {
            if (State == ServerState.Disposed) return Task.FromResult(new ServerCloseResult(ServerCloseStatus.Completed, 0, 0, 0));
            Volatile.Write(ref _state, (int)ServerState.Stopping);
        }
        return CloseCoreAsync(timeout, cancellationToken);
    }
    private async Task<ServerCloseResult> CloseCoreAsync(TimeSpan timeout, CancellationToken token)
    {
        var result = await _host.CloseAsync(timeout, token).ConfigureAwait(false);
        if (result.Status == ServerCloseStatus.Completed)
        { Interlocked.CompareExchange(ref _state, (int)ServerState.Stopped, (int)ServerState.Stopping); _stopped.TrySetResult(); }
        return result;
    }
    /// <summary>Validates and atomically replaces one native TLS listener snapshot; existing connections retain their original snapshot.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    /// <param name="listenerName">The listenerName value described by this operation.</param>
    /// <param name="options">The options value described by this operation.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public Task ReloadTlsAsync(string listenerName, ServerTlsOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); ServerValidation.Text(listenerName, 128, nameof(listenerName)); cancellationToken.ThrowIfCancellationRequested();
        if (State != ServerState.Running) throw new InvalidOperationException("TLS reload requires a running application.");
        if ((Capabilities & ServerCapabilities.Tls) == 0) throw new NotSupportedException("The selected native asset has no TLS capability.");
        return _host.ReloadTlsAsync(listenerName, TlsSnapshot.Create(options), cancellationToken);
    }
    /// <summary>Closes and joins managed/native retirement without cancellation; self-join from this application's dispatch is rejected.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.NativeServerApplication" path="/member/remarks" />
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Executing.Value, this)) throw new InvalidOperationException("An application cannot dispose itself inside its handler or cleanup callback.");
        lock (_lifecycle) return new(_dispose ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        Volatile.Write(ref _state, (int)ServerState.Stopping);
        await _host.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (_ownsServices)
            {
                if (Services is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (Services is IDisposable disposable) disposable.Dispose();
            }
        }
        finally { Volatile.Write(ref _state, (int)ServerState.Disposed); _stopped.TrySetResult(); }
    }
    internal async Task DispatchAsync(HostRequest request)
    {
        var previous = Executing.Value; Executing.Value = this;
        RequestContext? context = null; Exception? failure = null;
        try
        {
            var route = Registry.Routes.SingleOrDefault(x => x.Id == request.RouteId) ?? throw new InvalidOperationException("Native dispatch supplied an unknown route identity.");
            context = new(this, request, route);
            RequestHandler next = InvokeEndpointAsync;
            for (int i = _middleware.Count - 1; i >= 0; i--)
            {
                var layer = _middleware[i]; var inner = next;
                next = (ctx, token) => InvokeMiddlewareAsync(layer, inner, ctx, token);
            }
            await next(context, request.RequestAborted).ConfigureAwait(false);
            if (request.Kind == 0 || request.Kind == 2) await context.Response.CompleteAsync(request.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            if (context is not null && request.Kind is 0 or 2)
                await context.Response.FailAsync(error switch
                {
                    RequestBindingException => 400,
                    ServerException { Code: "unsupported_media_type" } => 415,
                    ServerException { Code: "body_limit_exceeded" } => 413,
                    ServerException { Code: "resource_limit" } => 503,
                    _ => 500
                }).ConfigureAwait(false);
            else request.Abort();
        }
        finally
        {
            try { if (context is not null) failure = await context.Response.CleanupAsync(failure).ConfigureAwait(false); }
            finally
            {
                try { await request.CompleteDispatchAsync().ConfigureAwait(false); }
                catch { request.Abort(); }
                finally { Executing.Value = previous; }
            }
        }
    }
    private async Task InvokeEndpointAsync(RequestContext context, CancellationToken token)
    {
        if (Configuration.Authenticator is { } authenticator)
        {
            var user = await authenticator.AuthenticateAsync(context, token).ConfigureAwait(false);
            if (user is not null) context.SetUser(user);
        }
        foreach (string policy in context.Route.EffectivePolicies)
            if (!await Configuration.Authorizer!.AuthorizeAsync(context, policy, token).ConfigureAwait(false))
            {
                if (context.Native.Kind is not (0 or 2)) throw new ServerException("authorization_denied", "The protocol request was denied.");
                context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true ? 403 : 401; return;
            }
        if (context.Route.Raw is { } raw) { await raw(context, token).ConfigureAwait(false); return; }
        ResponseHandler next = context.Route.Handler ?? throw new InvalidOperationException("This route has no typed dispatch handler.");
        for (int i = context.Route.EffectiveFilters.Length - 1; i >= 0; i--)
        {
            var filter = context.Route.EffectiveFilters[i]; var inner = next;
            next = (ctx, ct) => InvokeFilterAsync(filter, inner, ctx, ct);
        }
        var result = await next(context, token).ConfigureAwait(false) ?? throw new ServerException("handler_contract", "A handler returned a null result.");
        if (context.Response.Started) throw new ServerException("response_already_started", "A result cannot follow committed direct output.");
        await result.ExecuteAsync(context, token).ConfigureAwait(false);
    }
    private static async Task InvokeMiddlewareAsync(ServerMiddleware layer, RequestHandler inner, RequestContext context, CancellationToken token)
    {
        var continuation = new DispatchContinuation<bool>(async (ctx, ct) => { await inner(ctx, ct).ConfigureAwait(false); return true; });
        Exception? failure = null;
        try { await layer(context, (ctx, ct) => continuation.Invoke(ctx, ct), token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        await continuation.SealAndJoinAsync(failure).ConfigureAwait(false);
    }
    private static async Task<ServerResult> InvokeFilterAsync(ServerFilter filter, ResponseHandler inner, RequestContext context, CancellationToken token)
    {
        var continuation = new DispatchContinuation<ServerResult>((ctx, ct) => inner(ctx, ct));
        Exception? failure = null; ServerResult? result = null;
        try { result = await filter(context, continuation.Invoke, token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        await continuation.SealAndJoinAsync(failure).ConfigureAwait(false);
        return result!;
    }
}

// A single bounded continuation root prevents forgotten next work from escaping
// dispatch cleanup. It does not create an independent runtime or unbounded queue.
internal sealed class DispatchContinuation<T>(Func<RequestContext, CancellationToken, Task<T>> next)
{
    private readonly object _gate = new();
    private TaskCompletionSource<T>? _completion;
    private bool _sealed;
    internal Task<T> Invoke(RequestContext context, CancellationToken token)
    {
        TaskCompletionSource<T> completion;
        lock (_gate)
        {
            context.Alive();
            if (_sealed || _completion is not null) throw new InvalidOperationException("Next is single-call and cannot escape its middleware/filter.");
            completion = _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = ExecuteAsync(context, token, completion);
        return completion.Task;
    }
    private async Task ExecuteAsync(RequestContext context, CancellationToken token, TaskCompletionSource<T> completion)
    {
        try { completion.TrySetResult(await next(context, token).ConfigureAwait(false)); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    internal async Task SealAndJoinAsync(Exception? failure)
    {
        Task<T>? pending; bool escaped;
        lock (_gate) { _sealed = true; pending = _completion?.Task; escaped = pending is { IsCompleted: false }; }
        // Even a contract violation retains the request until already-started next
        // work finishes. Returning a synthetic task completion is not retirement.
        if (pending is not null)
            try { await pending.ConfigureAwait(false); } catch (Exception error) { if (escaped) failure ??= error; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (escaped) throw new InvalidOperationException("Middleware/filter must await its next operation.");
    }
}
