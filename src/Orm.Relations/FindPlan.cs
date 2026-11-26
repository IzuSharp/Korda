using Korda.Data;
using Korda.Database.Internal;
using Korda.Query;
using Korda.Query.Internal;

namespace Korda.Database;

public sealed partial class DataSource
{
    private readonly Dictionary<object, LinkedListNode<FindCacheEntry>> _findPlans = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<FindCacheEntry> _findOrder = new();
    private long _findBytes;
    private sealed record FindCacheEntry(object Table, IFindPlan Plan);
    internal (int Entries, long Bytes) FindCacheCounts { get { lock (_registrations) return (_findPlans.Count, _findBytes); } }

    internal FindPlan<TEntity> AcquireFindPlan<TEntity>(Table<TEntity> table) where TEntity : class
        => AcquireFindPlan(table, null, out _);

    internal FindPlan<TEntity> AcquireFindPlan<TEntity>(Table<TEntity> table, EntityKey<TEntity>? key, out long payloadBytes) where TEntity : class
    {
        payloadBytes = 0;
        CheckOpen(); Capabilities.Require(FindShapeCalls.Capability);
        lock (_registrations)
        {
            CheckOpen();
            if (_findPlans.TryGetValue(table, out var hit))
            {
                if (key is not null) payloadBytes = FindPlan<TEntity>.KeyPayloadBytes(this, key);
                _findOrder.Remove(hit); _findOrder.AddLast(hit);
                var existing = (FindPlan<TEntity>)hit.Value.Plan;
                existing.Retain(); return existing;
            }
            // The former From(table) admitted its model before reading the key
            // frames. Preserve that failure precedence only on a cache miss.
            var model = RegisterModel(table);
            if (key is not null) payloadBytes = FindPlan<TEntity>.KeyPayloadBytes(this, key);
            var created = FindPlan<TEntity>.Create(this, table, model);
            try
            {
                CheckOpen();
                // Like the native compiled-shape cache, an oversized individual
                // shape remains executable but is not retained by the cache.
                if (created.Charge <= Limits.MaxPreparedPlanBytes)
                {
                    while (_findPlans.Count >= Limits.MaxPreparedPlans || created.Charge > Limits.MaxPreparedPlanBytes - _findBytes)
                    {
                        var oldest = _findOrder.First!; _findOrder.RemoveFirst(); _findPlans.Remove(oldest.Value.Table);
                        _findBytes -= oldest.Value.Plan.Charge; oldest.Value.Plan.Dispose();
                    }
                    var entry = new LinkedListNode<FindCacheEntry>(new(table, created));
                    _findPlans.Add(table, entry); _findOrder.AddLast(entry);
                    created.Retain(); _findBytes += created.Charge;
                }
                return created;
            }
            catch { created.Dispose(); throw; }
        }
    }

    partial void ReleaseFindPlans()
    {
        foreach (var item in _findOrder) item.Plan.Dispose();
        _findPlans.Clear(); _findOrder.Clear(); _findBytes = 0;
    }
}

internal interface IFindPlan : IDisposable { long Charge { get; } }

// Each caller retains the immutable shape until its terminal completes. Cache
// eviction and source closure release only the cache reference. Accepted pulls
// retain their normal native execution state; this owner never retains a session.
internal sealed class FindPlan<TEntity> : IFindPlan where TEntity : class
{
    private readonly DataSource _source;
    private readonly Table<TEntity> _table;
    private readonly DatabaseOwner _owner;
    private readonly DbWire.ParameterLayout _parameters;
    private readonly StreamProjectionLayout _projection;
    private readonly long _logicalBytes;
    private int _references = 1;
    public long Charge { get; }

    private FindPlan(DataSource source, Table<TEntity> table, DatabaseOwner owner,
        DbWire.ParameterLayout parameters, StreamProjectionLayout projection, long charge, long logicalBytes)
    { _source = source; _table = table; _owner = owner; _parameters = parameters; _projection = projection; Charge = charge; _logicalBytes = logicalBytes; }

    internal static unsafe FindPlan<TEntity> Create(DataSource source, Table<TEntity> table, DatabaseOwner model)
    {
        var calls = source.Binding.Finds ?? throw new DatabaseCapabilityException(FindShapeCalls.Capability, source.Provider.ToString());
        var projectionId = source.RegisterProjection(table.Row);
        var codecs = table.Row.Fields.Select(field => source.BindCodec(field.Descriptor)).ToArray();
        var layout = TypedStream<TEntity>.CreateProjectionLayout(table.Row, source.Binding.SemanticProfile,
            projectionId, codecs, new(source.Limits.MaxBatchRows, checked((int)source.Limits.MaxBatchBytes),
                Math.Max(1, source.Limits.MaxBufferedRows), Math.Max(1, source.Limits.MaxBufferedBytes)));
        var definitions = new ParameterDefinition[table.KeyColumns.Length];
        for (var i = 0; i < definitions.Length; i++)
        {
            var codec = table.KeyColumns[i].Definition.Codec;
            definitions[i] = new(source, (uint)i + 1, "find_key_" + i, codec, source.BindCodec(codec));
        }
        // Reuse the same canonical parameter schema as public prepared shapes;
        // these private ordinal definitions never allocate public identities.
        var parameters = PreparedParameterLayout.CreateWireLayout(source, definitions);
        using var sourceLease = source.Owner.Borrow(DatabaseOwnerKind.Source);
        using var modelLease = model.Borrow(DatabaseOwnerKind.Model);
        nint result = 0; ulong nativeCharge = 0, logicalBytes = 0; var error = BindingChecks.Error();
        HostFailure.Check(source.Binding, calls.Create(sourceLease.Pointer, modelLease.Pointer, table.Identity.Id,
            projectionId, &result, &nativeCharge, &logicalBytes, &error), error);
        DatabaseOwner? owner = null;
        try
        {
            owner = new(source.Binding, DatabaseOwnerKind.Plan, result);
            // Parameter prefix, descriptors and projection expectation are the
            // only managed shape data. Table/mapper metadata is caller-owned.
            var charge = checked((long)nativeCharge + parameters.PayloadOffset + 512L +
                definitions.Length * 128L + codecs.Length * 128L);
            return new(source, table, owner, parameters, layout, charge, checked((long)logicalBytes));
        }
        catch { if (owner is not null) owner.Dispose(); else if (result != 0) source.Binding.Calls.nw_db_owner_release_v1(result); throw; }
    }

    internal void Retain()
    {
        if (Interlocked.Increment(ref _references) <= 1) throw new ObjectDisposedException(nameof(FindPlan<TEntity>));
    }
    public void Dispose() { if (Interlocked.Decrement(ref _references) == 0) _owner.Dispose(); }

    internal static long KeyPayloadBytes(DataSource source, EntityKey<TEntity> key)
    {
        const int frameBytes = WireSchema.HeaderBytes + WireSchema.DescriptorBytes + WireSchema.CellBytes;
        const int whereBytes = WireSchema.ControlHeaderBytes + WireSchema.ControlEntryBytes + sizeof(ulong);
        long bytes = 0; var nodes = 0; var expanded = 1; var owned = 0; var limits = source.Limits;
        foreach (var value in key.Values)
        {
            var size = value.Bytes?.Length ?? 0;
            // The former Find passed one ExpressionValues frame to Value for
            // each key. Its wire-size rejection preceded logical query quota.
            if (size > DbWire.MaxEncodedBytes - frameBytes) throw new ArgumentException("Payload exceeds the configured wire byte limit.");
            Admit(0, 1, 1); // Column; evaluated before Binding.Value.
            if (size > limits.MaxParameterBytes - frameBytes) throw new ArgumentException("Expression wire size.");
            Admit(checked(frameBytes + size), 1, 1);
            Admit(0, 2, 3); // Equality references the two scalar nodes.
            Admit(whereBytes, 0, 3); // WHERE references that equality subtree.
            bytes = checked(bytes + size);
        }
        return bytes;

        void Admit(int addedBytes, int depth, int addedExpanded)
        {
            if (nodes >= limits.MaxQueryNodes || addedBytes > limits.MaxParameterBytes - owned ||
                depth > limits.MaxQueryDepth || addedExpanded > limits.MaxQueryNodes - expanded)
                throw HostFailure.Binding(source.Binding, new(Korda.Interop.Abi.NativeStatus.DomainError, 0x4442, 11, 0));
            nodes++; expanded += addedExpanded; owned += addedBytes;
        }
    }

    internal Task<TEntity?> Execute(DataSession session, EntityKey<TEntity> key, CancellationToken token)
        => Execute(session, key, token, KeyPayloadBytes(_source, key));

    internal Task<TEntity?> Execute(DataSession session, EntityKey<TEntity> key, CancellationToken token, long bytes)
    {
        session.CheckOpen();
        if (!ReferenceEquals(_source, session.Source) || !ReferenceEquals(_table, key.Table))
            throw new ArgumentException("A Find shape requires its exact source and table key.", nameof(key));
        if (bytes > checked(_source.Limits.MaxParameterBytes - _logicalBytes))
            throw HostFailure.Binding(_source.Binding, new(Korda.Interop.Abi.NativeStatus.DomainError, 0x4442, 11, 0));
        var values = _parameters.CreateFrame(checked((int)bytes)); var position = _parameters.PayloadOffset;
        for (var i = 0; i < key.Values.Length; i++)
        {
            var value = key.Values[i].Bytes;
            _parameters.ValidateValue(i, value); _parameters.WriteValue(values, i, value, ref position);
        }
        var stream = new TypedStream<TEntity>((rows, cancellation) => session.OpenPlanStreamAsync(_owner, values, rows, cancellation),
            _table.Row, _projection, session.StreamingLimits, token);
        return stream.SingleOrDefaultAsync();
    }
}
