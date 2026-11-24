using Korda.Data;
using Korda.Database;

internal sealed partial class Workload
{
    private static Order SeedOrder(long id) => new(1,id,Dataset.CreatedAt(id),Dataset.CustomerId(id),Dataset.Cents(1,id)/100m,
        Dataset.Note(id),1,new(Dataset.Channel(id),Dataset.Gift(id)),new string('m',id%17==0?2048:32));
    private void InitializeLifecycle()
    {
        foreach(var name in _options.Verify?Names:_names)
        {
            if(name is not("tx-commit" or "tx-rollback" or "partial-update" or "stale-conflict" or "early-stop"))continue;
            if(name=="early-stop")
            {
                var early=new EarlyCase(_session,_options,_options.Mode=="prepared",out var sql);
                _advanced.Add(name,early);_sql[name]=sql;
                var digest=Digest.Create();for(long id=1;id<=3;id++)HashOrder(ref digest,SeedOrder(id));
                _expected[name]=new(3,digest.Value,0);continue;
            }
            _advanced.Add(name,new TransactionCase(name,_options.OrdersPerTenant));
            _sql[name]="public CRUD: BEGIN; Find full order by composite key; update; validate; restore or rollback; confirm persisted full row. No prepared CRUD overload.";
            var original=SeedOrder(_options.OrdersPerTenant);var expected=Digest.Create();HashOrder(ref expected,original);
            if(name=="stale-conflict") { expected.Boolean(true);HashOrder(ref expected,original);_expected[name]=new(2,expected.Value,0); }
            else
            {
                var changed=name=="partial-update"?original with{Note=null}:original with{Note="perf-mutated",Version=2};
                HashOrder(ref expected,changed);HashOrder(ref expected,original);_expected[name]=new(3,expected.Value,0);
            }
        }
    }
    private sealed class EarlyCase : IReadCase
    {
        private readonly int _take;
        private readonly Korda.Query.PreparedQuery<Order>? _plan;
        internal EarlyCase(DataSession session,Options options,bool prepared,out string sql)
        {
            _take=options.OrdersPerTenant;
            using var query=session.From(Orders.Table).WhereEqual(Orders.TenantId,1L).OrderBy(Orders.Id).Take(_take);
            var selection=query.SelectAll();sql=selection.ToSql();if(prepared)_plan=selection.Prepare();
        }
        public ValueTask<OperationResult> Execute(DataSession session,bool prepared)=>prepared
            ?Read(_plan!.AsAsyncEnumerable(session),HashOrder,3):Dynamic(session);
        private async ValueTask<OperationResult> Dynamic(DataSession session)
        {
            using var query=session.From(Orders.Table).WhereEqual(Orders.TenantId,1L).OrderBy(Orders.Id).Take(_take);
            return await Read(query.SelectAll().AsAsyncEnumerable(),HashOrder,3).ConfigureAwait(false);
        }
        public void Dispose()=>_plan?.Dispose();
    }
    private sealed class TransactionCase(string name,long id) : IReadCase
    {
        public async ValueTask<OperationResult> Execute(DataSession session,bool prepared)
        {
            long suspensions=0;var digest=Digest.Create();
            var key=Orders.Table.Key(Orders.TenantId,1L).And(Orders.Id,id);
            var begin=session.BeginTransactionAsync();if(!begin.IsCompleted)suspensions++;
            var transaction=await begin.ConfigureAwait(false);
            Order original;
            try
            {
            var find=session.FindAsync(Orders.Table,key);if(!find.IsCompleted)suspensions++;
            original=await find.ConfigureAwait(false)??throw new InvalidOperationException("Write target disappeared.");
            if(original!=SeedOrder(id))throw new InvalidOperationException("Write target differs from fixed initial state.");
            HashOrder(ref digest,original);
            var changes=new Changes<Order>().Set(Orders.Note,name=="partial-update"?(string?)null:"perf-mutated");
            if(name!="partial-update")changes.Set(Orders.Version,2L);
            var update=name=="stale-conflict"
                ?session.UpdateCheckedAsync(Orders.Table,key,changes,new ConcurrencyCheck<Order>().Expect(Orders.Version,1L))
                :session.UpdateAsync(Orders.Table,key,changes);
            if(!update.IsCompleted)suspensions++;
            if(await update.ConfigureAwait(false)!=1)throw new InvalidOperationException("Mutation did not affect exactly one order.");
            if(name=="stale-conflict")
            {
                var caught=false;
                try
                {
                    var stale=session.UpdateCheckedAsync(Orders.Table,key,new Changes<Order>().Set(Orders.Note,"stale-write"),new ConcurrencyCheck<Order>().Expect(Orders.Version,1L));
                    if(!stale.IsCompleted)suspensions++;await stale.ConfigureAwait(false);
                }
                catch(ConcurrencyConflictException){caught=true;}
                if(!caught)throw new InvalidOperationException("A stale optimistic write was accepted.");
                digest.Boolean(true);
                var rollback=transaction.RollbackAsync();if(!rollback.IsCompleted)suspensions++;await rollback.ConfigureAwait(false);
                if(transaction.Outcome!=TransactionOutcome.RolledBack)throw new InvalidOperationException("Rollback was not confirmed.");
            }
            else
            {
                var check=session.FindAsync(Orders.Table,key);if(!check.IsCompleted)suspensions++;
                var changed=await check.ConfigureAwait(false)??throw new InvalidOperationException("Mutation removed its row.");
                var expected=name=="partial-update"?original with{Note=null}:original with{Note="perf-mutated",Version=2};
                if(changed!=expected)throw new InvalidOperationException("Partial mutation changed omitted fields or lost NULL.");
                HashOrder(ref digest,changed);
                if(name=="tx-rollback")
                {
                    var rollback=transaction.RollbackAsync();if(!rollback.IsCompleted)suspensions++;await rollback.ConfigureAwait(false);
                    if(transaction.Outcome!=TransactionOutcome.RolledBack)throw new InvalidOperationException("Rollback was not confirmed.");
                }
                else
                {
                    var restore=new Changes<Order>().Set(Orders.Note,original.Note);
                    if(name!="partial-update")restore.Set(Orders.Version,original.Version);
                    var restoring=session.UpdateAsync(Orders.Table,key,restore);if(!restoring.IsCompleted)suspensions++;
                    if(await restoring.ConfigureAwait(false)!=1)throw new InvalidOperationException("Restore did not affect exactly one row.");
                    var commit=transaction.CommitAsync();if(!commit.IsCompleted)suspensions++;await commit.ConfigureAwait(false);
                    if(transaction.Outcome!=TransactionOutcome.Committed)throw new InvalidOperationException("Commit was not confirmed.");
                }
            }
            }
            finally
            {
                // Outcome confirmation is distinct from local retirement. Join the
                // transaction before reusing this physical session for the persisted read.
                var retirement=transaction.DisposeAsync();if(!retirement.IsCompleted)suspensions++;
                await retirement.ConfigureAwait(false);
            }
            if(!transaction.Cleanup.LocalResourcesReleased)throw new InvalidOperationException("Transaction retirement did not release local resources.");
            var final=session.FindAsync(Orders.Table,key);if(!final.IsCompleted)suspensions++;
            var persisted=await final.ConfigureAwait(false)??throw new InvalidOperationException("Persisted order disappeared.");
            if(persisted!=original)throw new InvalidOperationException("Durable state was not restored after transaction.");
            HashOrder(ref digest,persisted);
            return new(name=="stale-conflict"?2:3,digest.Value,suspensions);
        }
        public void Dispose(){ }
    }
}
