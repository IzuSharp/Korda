using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NativeWeave.Server;

/// <summary>Request-scoped HTTP response with one writer, one completion attempt and deterministic cleanup.</summary>
public sealed class ServerResponse
{
    private readonly RequestContext _context;
    private readonly object _terminalLock = new();
    private readonly List<Func<CancellationToken, Task>> _starting = [];
    private readonly List<Func<Task>> _completed = [];
    private readonly List<IAsyncDisposable> _resources = [];
    private Task? _startTask;
    private Task? _completeTask;
    private Stream? _body;
    private int _status = 200;
    private int _writer;
    private long _written;
    private bool _committed;
    private bool _complete;
    private bool _cleaning;
    internal ServerResponse(RequestContext context)
    {
        _context = context;
        Headers = new(context.Http, HeaderMutable, context.Configuration.Options.MaxHeaderBytes);
        Trailers = new(context.Http, TrailerMutable, context.Configuration.Options.MaxHeaderBytes, true);
    }
    /// <summary>Gets or sets a final status in 200..599 before headers commit.</summary>
    public int StatusCode { get { _context.Http(); return _status; } set { HeaderMutable(); ServerValidation.Status(value); _status = value; } }
    /// <summary>Gets response headers mutable before their commit.</summary>
    public HeaderCollection Headers { get; }
    /// <summary>Gets trailers mutable before completion; transport capability remains native-authoritative.</summary>
    public HeaderCollection Trailers { get; }
    /// <summary>Gets or sets Content-Type in the same header map.</summary>
    public string? ContentType { get { _context.Http(); return Headers.GetSingle("Content-Type"); } set { HeaderMutable(); if (value is null) Headers.Remove("Content-Type"); else Headers.Set("Content-Type", value); } }
    /// <summary>Gets or sets a fixed body length; write mismatch aborts instead of truncating or padding.</summary>
    public long? ContentLength
    {
        get { _context.Http(); return Headers.GetSingle("Content-Length") is { } value ? long.Parse(value, CultureInfo.InvariantCulture) : null; }
        set { HeaderMutable(); if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value)); if (value is null) Headers.Remove("Content-Length"); else Headers.Set("Content-Length", value.Value.ToString(CultureInfo.InvariantCulture)); }
    }
    /// <summary>Gets whether native final response headers have committed.</summary>
    public bool HasStarted { get { _context.Http(); return _committed; } }
    /// <summary>Gets the borrowed asynchronous output stream.</summary>
    public Stream Body { get { _context.Http(); return _body ??= new ServerWriteStream(_context.Native.Body, WriteAsync, FlushAsync); } }
    internal bool BodySuppressed => _context.Native.Method == "HEAD";
    internal bool Started => _committed;
    internal bool IsComplete => _complete;
    /// <summary>Runs starting callbacks in reverse order and commits headers once.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _context.Http(); cancellationToken.ThrowIfCancellationRequested();
        lock (_terminalLock) return _startTask ??= StartCoreAsync(cancellationToken);
    }
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        // Never invoke application callbacks while holding the coalescing lock.
        await Task.Yield();
        for (int i = _starting.Count - 1; i >= 0; i--) await _starting[i](cancellationToken).ConfigureAwait(false);
        ValidateFraming();
        await _context.Native.CommitHeadersAsync(_status, Headers.ToDictionary(), cancellationToken).ConfigureAwait(false);
        _committed = true;
    }
    /// <summary>Writes bounded chunks with one pending writer; native payload admission never waits for byte credit.</summary>
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        _context.Http(); cancellationToken.ThrowIfCancellationRequested();
        if (_complete || _completeTask is not null) throw new InvalidOperationException("Response completion has started.");
        if (data.Length != 0 && _status is 204 or 205 or 304) throw new InvalidOperationException("This final status forbids body content.");
        if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) throw new InvalidOperationException("Only one response write may be pending.");
        return WriteCoreAsync(data, cancellationToken);
    }
    private async Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            if (BodySuppressed || data.IsEmpty) return;
            if (ContentLength is { } expected && data.Length > expected - _written) throw new InvalidOperationException("Response exceeds Content-Length.");
            for (int offset = 0; offset < data.Length;)
            {
                int count = Math.Min(_context.Native.Body.MaxChunkBytes, data.Length - offset);
                using var overlap = _context.Native.Body.AcquireManagedCredit(count);
                await _context.Native.Body.WriteAsync(data.Slice(offset, count), cancellationToken).ConfigureAwait(false);
                offset += count; _written = checked(_written + count);
            }
        }
        catch { if (_committed) _context.Native.Abort(); throw; }
        finally { Volatile.Write(ref _writer, 0); }
    }
    /// <summary>Flushes local native flow-controlled progress without certifying remote consumption.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _context.Http(); cancellationToken.ThrowIfCancellationRequested();
        if (_complete || _completeTask is not null) throw new InvalidOperationException("Response completion has started.");
        if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) throw new InvalidOperationException("Only one response operation may be pending.");
        return FlushCoreAsync(cancellationToken);
    }
    private async Task FlushCoreAsync(CancellationToken token)
    { try { await StartAsync(token).ConfigureAwait(false); await _context.Native.Body.FlushAsync(token).ConfigureAwait(false); } finally { Volatile.Write(ref _writer, 0); } }
    /// <summary>Writes JSON using explicit static metadata and finite encoded/peak limits.</summary>
    public Task WriteJsonAsync<T>(T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        _context.Http(); ArgumentNullException.ThrowIfNull(typeInfo); cancellationToken.ThrowIfCancellationRequested();
        HeaderMutable(); ServerValidation.ContentStatus(_status);
        if (_complete || _completeTask is not null) throw new InvalidOperationException("Response completion has started.");
        if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) throw new InvalidOperationException("Only one response write may be pending.");
        try { ContentType ??= "application/json"; return WriteJsonCoreAsync(value, typeInfo, cancellationToken); }
        catch { Volatile.Write(ref _writer, 0); throw; }
    }
    private async Task WriteJsonCoreAsync<T>(T value, JsonTypeInfo<T> typeInfo, CancellationToken token)
    {
        try
        {
            if (BodySuppressed) { await StartAsync(token).ConfigureAwait(false); return; }
            int maximum = _context.Route.EffectiveMaxJsonBytes;
            using var credit = _context.Native.Body.AcquireManagedCredit(checked(maximum * 3));
            using var buffer = new BoundedJsonBuffer(maximum);
            using (var writer = new Utf8JsonWriter(buffer)) JsonSerializer.Serialize(writer, value, typeInfo);
            await WriteCoreAsync(buffer.WrittenMemory, token).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _writer, 0); }
    }
    /// <summary>Joins one terminal attempt; failed completion is retained and never retried as a second response.</summary>
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        _context.Http(); cancellationToken.ThrowIfCancellationRequested();
        lock (_terminalLock)
        {
            if (_completeTask is not null) return _completeTask;
            if (Volatile.Read(ref _writer) != 0) throw new InvalidOperationException("Await the response write before completion.");
            return _completeTask = CompleteCoreAsync(cancellationToken);
        }
    }
    private async Task CompleteCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            if (!BodySuppressed && _status != 304 && ContentLength is { } expected && expected != _written) throw new InvalidOperationException("Response length does not match Content-Length.");
            if (Trailers.Count != 0) await _context.Native.CommitTrailersAsync(Trailers.ToDictionary(), cancellationToken).ConfigureAwait(false);
            await _context.Native.Body.FinishAsync(cancellationToken).ConfigureAwait(false);
            _complete = true;
        }
        catch { if (_committed) _context.Native.Abort(); throw; }
    }
    /// <summary>Registers one of at most 32 LIFO starting callbacks before starting begins.</summary>
    public void OnStarting(Func<CancellationToken, Task> callback)
    {
        _context.Http(); ArgumentNullException.ThrowIfNull(callback);
        if (_startTask is not null || _cleaning || _starting.Count >= 32) throw new InvalidOperationException("Starting callbacks are frozen or full.");
        _starting.Add(callback);
    }
    /// <summary>Registers one of at most 32 LIFO completion callbacks, before disposal and request-scope release.</summary>
    public void OnCompleted(Func<Task> callback)
    { _context.Alive(); ArgumentNullException.ThrowIfNull(callback); if (_cleaning || _completed.Count >= 32) throw new InvalidOperationException("Completion registrations are frozen or full."); _completed.Add(callback); }
    /// <summary>Transfers one disposable after successful bounded registration; duplicate identity is rejected.</summary>
    public void RegisterForDisposeAsync(IAsyncDisposable resource)
    {
        _context.Alive(); ArgumentNullException.ThrowIfNull(resource);
        if (_cleaning || _resources.Count >= 32 || _resources.Any(x => ReferenceEquals(x, resource))) throw new InvalidOperationException("Disposal registration is frozen, full or duplicated.");
        _resources.Add(resource);
    }
    internal async Task<Exception?> CleanupAsync(Exception? original)
    {
        _cleaning = true;
        for (int i = _completed.Count - 1; i >= 0; i--)
            try { await _completed[i]().ConfigureAwait(false); } catch (Exception error) { original ??= error; }
        for (int i = _resources.Count - 1; i >= 0; i--)
            try { await _resources[i].DisposeAsync().ConfigureAwait(false); } catch (Exception error) { original ??= error; }
        try { await _context.EndScopeAsync().ConfigureAwait(false); } catch (Exception error) { original ??= error; }
        return original;
    }
    internal async Task FailAsync(int status)
    {
        if (_committed) { _context.Native.Abort(); return; }
        // Single bounded fallback bypasses failed application starting callbacks.
        _status = status; Headers.Clear();
        try
        {
            await _context.Native.CommitHeadersAsync(status, Headers.ToDictionary(), CancellationToken.None).ConfigureAwait(false);
            _committed = true;
            await _context.Native.Body.FinishAsync(CancellationToken.None).ConfigureAwait(false);
            _complete = true;
        }
        catch { _context.Native.Abort(); }
    }
    private void HeaderMutable() { _context.Http(); if (_committed || _cleaning) throw new InvalidOperationException("Response headers have committed or cleanup has begun."); }
    private void TrailerMutable() { _context.Http(); if (_completeTask is not null || _cleaning) throw new InvalidOperationException("Response trailers have frozen."); }
    private void ValidateFraming()
    {
        ServerValidation.Status(_status);
        long? length = ContentLength;
        if (_status == 204 && length.HasValue || _status == 205 && length is not null and not 0) throw new InvalidOperationException("Content-Length is forbidden by this status.");
        if ((BodySuppressed || _status is 204 or 205 or 304) && Trailers.Count != 0) throw new InvalidOperationException("This response cannot carry trailers.");
    }
}

internal sealed class BoundedJsonBuffer(int maximum) : System.Buffers.IBufferWriter<byte>, IDisposable
{
    private readonly byte[] _buffer = new byte[maximum];
    private int _written;
    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
    public void Advance(int count) { if (count < 0 || count > maximum - _written) throw new ServerException("body_limit_exceeded", "The JSON output exceeds its finite byte limit."); _written += count; }
    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(_written); }
    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(_written); }
    private void Ensure(int hint) { if (hint < 0 || Math.Max(hint, 1) > maximum - _written) throw new ServerException("body_limit_exceeded", "The JSON output exceeds its finite byte limit."); }
    public void Dispose() { }
}
