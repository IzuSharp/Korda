using System.Net;

namespace NativeWeave.Server;

internal abstract class ServerHost : IAsyncDisposable
{
    internal abstract ServerCapabilities Capabilities { get; }
    internal abstract IReadOnlyList<Uri> BoundAddresses { get; }
    internal abstract Task Lifetime { get; }
    internal abstract Task StartAsync(IReadOnlyList<RouteDefinition> routes, Func<HostRequest, Task> dispatch, CancellationToken cancellationToken);
    internal abstract Task<ServerCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken);
    internal abstract Task ReloadTlsAsync(string listenerName, TlsSnapshot options, CancellationToken cancellationToken);
    internal abstract void ReleaseUnstarted();
    public abstract ValueTask DisposeAsync();
}

internal static partial class NativeHostFactory
{
    // B17 supplies the statically linked factory; no reflection or alternate managed listener.
    internal static ServerHost Create(ServerConfiguration configuration)
    {
        ServerHost? host = null;
        CreateCore(configuration, ref host);
        return host ?? throw new InvalidOperationException("The NativeWeave Server native host component is not installed.");
    }
    static partial void CreateCore(ServerConfiguration configuration, ref ServerHost? host);
}

internal abstract class HostRequest
{
    internal volatile Func<ulong, Task>? ProtocolContinuation;
    internal Func<Task>? ProtocolJoin { get; set; }
    internal Task JoinProtocolContinuationAsync() => ProtocolJoin?.Invoke() ?? Task.CompletedTask;
    internal abstract long RouteId { get; }
    internal abstract int Kind { get; }
    internal abstract ServerBodyTransport Body { get; }
    internal abstract string Method { get; }
    internal abstract string Scheme { get; }
    internal abstract string Host { get; }
    internal abstract string Path { get; }
    internal abstract string QueryString { get; }
    internal abstract string Protocol { get; }
    internal abstract IPEndPoint? RemoteEndPoint { get; }
    internal abstract IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }
    internal abstract IReadOnlyDictionary<string, IReadOnlyList<string>> RouteValues { get; }
    internal abstract IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; }
    internal abstract CancellationToken RequestAborted { get; }
    internal abstract TimeSpan TimeRemaining { get; }
    internal abstract string TraceIdentifier { get; }
    internal abstract IReadOnlyDictionary<string, IReadOnlyList<string>> GetTrailers();
    internal abstract Task CommitHeadersAsync(int status, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, CancellationToken cancellationToken);
    internal abstract Task CommitTrailersAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> trailers, CancellationToken cancellationToken);
    internal abstract Task WriteServerEventAsync(ServerEvent value, CancellationToken cancellationToken);
    internal abstract void Abort();
    internal abstract Task CompleteDispatchAsync();
}
