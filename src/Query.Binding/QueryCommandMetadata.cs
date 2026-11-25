using System.Collections.Concurrent;
using Korda.Data;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

internal sealed class QueryCommandEnvironment(QueryCommandCalls calls, int nodes, int depth, int parameters, int bytes, IEnumerable<string> capabilities)
{
    private readonly HashSet<string> _capabilities = new(capabilities, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, CodecDescriptor> _codecs = new();
    private readonly Dictionary<uint, QueryParameterMetadata> _parameters = [];
    private long _originSequence;
    internal QueryCommandCalls Calls { get; } = calls;
    internal int MaxNodes { get; } = nodes;
    internal int MaxDepth { get; } = depth;
    internal int MaxParameters { get; } = parameters;
    internal int MaxBytes { get; } = bytes;
    internal int MaxEnvelopeBytes { get; } = checked(bytes + 128 * nodes + 64);
    internal QueryOrigin Origin()
    {
        var sequence = Interlocked.Increment(ref _originSequence);
        if (sequence <= 0) QueryCommand.Capacity();
        return new(sequence);
    }

    internal void Require(string capability)
    {
        if (!_capabilities.Contains(capability)) throw new BindingFailure(NativeStatus.NotSupported, 0, 11, 0);
    }

    internal void RegisterCodec(uint bound, CodecDescriptor codec) => _codecs.TryAdd(bound, codec with { Nullable = false });
    internal CodecDescriptor Codec(uint bound, bool nullable)
    {
        if (!_codecs.TryGetValue(bound, out var codec)) throw new ArgumentException("Unknown source-bound query codec.");
        return codec.Nullable == nullable ? codec : codec with { Nullable = nullable };
    }

    internal uint Bound(CodecDescriptor codec)
    {
        if (codec.Configuration is null) return codec.Id;
        foreach (var pair in _codecs) if (pair.Value.SameScalar(codec)) return pair.Key;
        throw new ArgumentException("Unknown configured query codec.");
    }

    internal void RegisterParameter(QueryParameterMetadata parameter)
    {
        lock (_parameters)
        {
            if (_parameters.TryGetValue(parameter.Id, out var old))
            {
                if (old != parameter) throw new ArgumentException("The parameter ID has another source-bound identity.");
                return;
            }
            if (_parameters.Count >= MaxNodes) QueryCommand.Capacity();
            _parameters.Add(parameter.Id, parameter);
        }
    }
}

internal sealed class QueryModelMetadata(uint tableId, string table, ColumnDefinition[] columns)
{
    internal uint TableId { get; } = tableId;
    internal string Table { get; } = table;
    internal ColumnDefinition[] Columns { get; } = columns;
    internal ColumnDefinition Column(uint id)
    {
        foreach (var column in Columns) if (column.Id == id) return column;
        throw new ArgumentException("Unknown query column.");
    }
}

internal sealed record QueryParameterMetadata(uint Id, string Name, CodecDescriptor Codec, uint BoundCodec);
internal sealed record QueryPlanMetadata(int Nodes, int Expanded, int StatementNodes, int OwnedBytes, int Depth,
    IReadOnlyDictionary<object, int> Literals, QueryParameterMetadata[] Parameters, CodecDescriptor[]? Projection);
internal sealed record QueryCteMetadata(string Name, QueryPlanMetadata Body);

// A value captured before a CTE boundary must keep the same native value identity
// when its original builder or a clone is submitted later. The finite record
// owns this reference; there is no source-wide retained-value cache. Metadata
// uses Identity, not this wrapper, to avoid owner/metadata/reference cycles.
internal sealed class QueryOrigin(long sequence)
{
    private int _references = 1;
    private QueryOriginCapture? _capture;
    internal object Identity { get; } = new();
    internal object Gate { get; } = new();
    internal long Sequence { get; } = sequence;
    internal int References => Volatile.Read(ref _references);
    internal QueryOriginCapture? Capture => Volatile.Read(ref _capture);
    internal QueryOrigin Retain() { Interlocked.Increment(ref _references); return this; }
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) Interlocked.Exchange(ref _capture, null)?.Owner.Release();
    }
    internal void Publish(RetainedQueryOwner owner, uint origin)
    {
        if (_capture is not null) return;
        Volatile.Write(ref _capture, new(owner.Retain(), origin));
    }
}
internal sealed record QueryOriginCapture(RetainedQueryOwner Owner, uint Origin);
