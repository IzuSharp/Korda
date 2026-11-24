using DbPerf.Campaign;
#if POSTGRESQL
using ProviderConnection = Npgsql.NpgsqlConnection;
#else
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace DbPerf.IndependentOrm;

internal sealed partial class DapperWorker
{
    private static readonly object PoolInitialization = new();
    private static Task<SessionPool>? s_pool;

    private static Task<SessionPool> SharedPoolAsync(Config config)
    {
        lock (PoolInitialization) return s_pool ??= SessionPool.CreateAsync(config);
    }

    private sealed class SessionPool : IAsyncDisposable
    {
        internal readonly Config Config;
        internal readonly int Capacity;
        private readonly object _gate = new();
        private readonly Queue<int> _free;
        private readonly SemaphoreSlim _available;
        private readonly ProviderConnection?[] _connections;
        private int _disposed;
        internal long ActiveLeases, Acquisitions, Returns, Retirements, Replacements, QuarantinedSlots;

        private SessionPool(Config config)
        {
            Config = config; Capacity = config.Concurrency;
            _free = new(Capacity); _available = new(Capacity, Capacity);
            _connections = new ProviderConnection?[Capacity];
            for (var index = 0; index < Capacity; index++) _free.Enqueue(index);
        }

        internal static async Task<SessionPool> CreateAsync(Config config)
        {
            var pool = new SessionPool(config);
            try
            {
                for (var index = 0; index < pool.Capacity; index++)
                    pool._connections[index] = await OpenAsync(config).ConfigureAwait(false);
                return pool;
            }
            catch (Exception original)
            {
                try { await pool.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Pool initialization and cleanup failed.", original, cleanup); }
                throw;
            }
        }

        internal readonly record struct Lease(int Slot, ProviderConnection Connection);

        internal async ValueTask<Lease> BorrowAsync()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!await _available.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                throw new TimeoutException("The bounded Dapper adapter pool admission exceeded five seconds.");
            int slot;
            lock (_gate) slot = _free.Dequeue();
            Interlocked.Increment(ref ActiveLeases);
            Interlocked.Increment(ref Acquisitions);
            try
            {
                var connection = _connections[slot];
                if (connection is null)
                {
                    connection = await OpenAsync(Config).ConfigureAwait(false);
                    _connections[slot] = connection;
                    Interlocked.Increment(ref Replacements);
                }
                return new(slot, connection);
            }
            catch
            {
                ReturnSlot(slot);
                throw;
            }
        }

        internal async ValueTask ReturnAsync(Lease lease, bool healthy)
        {
            if (!ReferenceEquals(_connections[lease.Slot], lease.Connection))
                throw new InvalidOperationException("A pool lease no longer owns its physical connection.");
            if (!healthy)
            {
                Interlocked.Increment(ref Retirements);
                try
                {
                    await CloseConnectionAsync(lease.Connection).ConfigureAwait(false);
                    _connections[lease.Slot] = null;
                }
                catch
                {
                    // A failed close cannot authorize another physical connection in the
                    // same slot. Retain its owner for final cleanup and reduce capacity.
                    Interlocked.Increment(ref QuarantinedSlots);
                    Interlocked.Decrement(ref ActiveLeases);
                    throw;
                }
            }
            ReturnSlot(lease.Slot);
        }

        private void ReturnSlot(int slot)
        {
            lock (_gate) _free.Enqueue(slot);
            Interlocked.Decrement(ref ActiveLeases);
            Interlocked.Increment(ref Returns);
            _available.Release();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Read(ref ActiveLeases) != 0)
                throw new InvalidOperationException("Do not dispose the shared pool while a worker owns a lease.");
            List<Exception>? failures = null;
            for (var index = 0; index < Capacity; index++)
            {
                var connection = _connections[index];
                if (connection is null) continue;
                try
                {
                    await CloseConnectionAsync(connection).ConfigureAwait(false);
                    _connections[index] = null;
                }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            _available.Dispose();
            if (failures is not null) throw new AggregateException("Physical pool cleanup failed.", failures);
        }
    }
}
