using Korda.Database.Internal;
using Korda.Query.Internal;

namespace Korda.Database;

internal sealed class HostStreamTransport : IStreamTransport
{
    private readonly object _gate = new();
    private readonly DataSource _source;
    private readonly DatabaseOwner _channel;
    private readonly CancellationToken _token;
    private readonly ScopedStreamBatch _batch;
    private readonly HostPullController _pull;
    private readonly HostCursorCloseController _close;
    private Task<StreamRetirement>? _join;
    internal unsafe HostStreamTransport(DataSource source, DatabaseOwner channel, int rows, CancellationToken token)
    {
        _source = source; _channel = channel; _token = token;
        _batch = new(source.Binding.Calls.nw_db_buffer_release_v1, source.Binding);
        _close = new(source, channel);
        _pull = new(source, channel, _batch, _close, rows, token);
    }
    internal HostPullSnapshot PullSnapshot => _pull.Snapshot;
    internal HostCloseSnapshot CloseSnapshot => _close.Snapshot;
    public ValueTask<ScopedStreamBatch> FetchAsync() => _pull.Request();
    public void CheckReadAdmission()
    {
        _source.CheckOpen(); _token.ThrowIfCancellationRequested();
        NativeHost.CheckPullRead(_channel);
    }
    public void RequestStop() => _pull.RequestStop();
    public Task<StreamRetirement> JoinAsync()
    {
        lock (_gate) return _join ??= JoinCoreAsync();
    }
    private async Task<StreamRetirement> JoinCoreAsync()
    {
        _pull.RequestStop();
        try { await _close.JoinAsync().ConfigureAwait(false); }
        finally
        {
            if (_close.Cleanup.LocalResourcesReleased)
            {
                _pull.Closed();
                _batch.Close();
            }
        }
        if (_pull.StopFailure is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        var snapshot = _close.Cleanup;
        return new(snapshot.LocalResourcesReleased, snapshot.ConnectionDisposition == ConnectionDisposition.Usable, 0);
    }
}
