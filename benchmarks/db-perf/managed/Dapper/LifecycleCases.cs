using System.Data;
using System.Data.Common;
using Dapper;

internal sealed partial class Workload
{
    private static Order SeedOrder(long id) => new(1,id,Dataset.CreatedAt(id),Dataset.CustomerId(id),Dataset.Cents(1,id)/100m,
        Dataset.Note(id),1,new(Dataset.Channel(id),Dataset.Gift(id)),new string('m',id%17==0?2048:32));
    private Parameters _earlyParameters = new();
    private void InitializeLifecycle()
    {
        _earlyParameters = new(p1:1,p2:_options.OrdersPerTenant);
        foreach(var name in _options.Verify?Names:_names)
        {
            if(name is not("tx-commit" or "tx-rollback" or "partial-update" or "stale-conflict" or "early-stop"))continue;
            var index=Array.IndexOf(Names,name);var digest=Digest.Create();
            if(name=="early-stop")
            {
                _sql[index]=Sql4;
                for(long id=1;id<=3;id++)HashOrder(ref digest,SeedOrder(id));
                _expected[index]=new(3,digest.Value,0);continue;
            }
            _sql[index]="BEGIN; full typed order query; parameterized partial UPDATE; compare-and-swap affected-row check when requested; restore or rollback; full persisted result check.";
            var original=SeedOrder(_options.OrdersPerTenant);HashOrder(ref digest,original);
            if(name=="stale-conflict") { digest.Boolean(true);HashOrder(ref digest,original);_expected[index]=new(2,digest.Value,0); }
            else
            {
                var changed=name=="partial-update"?original with{Note=null}:original with{Note="perf-mutated",Version=2};
                HashOrder(ref digest,changed);HashOrder(ref digest,original);_expected[index]=new(3,digest.Value,0);
            }
        }
    }
    private ValueTask<OperationResult> Lifecycle(DbConnection connection,int index)
        => Names[index]=="early-stop"?(_options.Mode=="prepared"?EarlyReused(connection):EarlyDynamic(connection))
            :TransactionCase(connection,Names[index]);
    [CacheCommand]
    private ValueTask<OperationResult> EarlyReused(DbConnection connection)
        =>Read(connection.QueryUnbufferedAsync<Order>(Sql4,_earlyParameters),HashOrder,3);
    [CacheCommand(false)]
    private ValueTask<OperationResult> EarlyDynamic(DbConnection connection)
        =>Read(connection.QueryUnbufferedAsync<Order>(BuildContractSql(4),new Parameters(p1:1,p2:_options.OrdersPerTenant)),HashOrder,3);
    private const string FindSql="SELECT \"orders\".\"TenantId\" AS \"TenantId\", \"orders\".\"Id\" AS \"Id\", \"orders\".\"CreatedAt\" AS \"CreatedAt\", \"orders\".\"CustomerId\" AS \"CustomerId\", \"orders\".\"Total\" AS \"Total\", \"orders\".\"Note\" AS \"Note\", \"orders\".\"Version\" AS \"Version\", \"orders\".\"Settings\" AS \"Settings\", \"orders\".\"InternalMemo\" AS \"InternalMemo\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @tenant AND \"orders\".\"Id\" = @id LIMIT @take";
    private const string UpdateSql="UPDATE \"orders\" SET \"Note\" = @note, \"Version\" = @version WHERE \"TenantId\" = @tenant AND \"Id\" = @id";
    private const string PartialSql="UPDATE \"orders\" SET \"Note\" = @note WHERE \"TenantId\" = @tenant AND \"Id\" = @id";
    private const string CheckedSql="UPDATE \"orders\" SET \"Note\" = @note, \"Version\" = @version WHERE \"TenantId\" = @tenant AND \"Id\" = @id AND \"Version\" = @expected";
    private const string StaleSql="UPDATE \"orders\" SET \"Note\" = @note WHERE \"TenantId\" = @tenant AND \"Id\" = @id AND \"Version\" = @expected";
    [CacheCommand(false)]
    private async ValueTask<OperationResult> TransactionCase(DbConnection connection,string name)
    {
        long suspensions=0;var digest=Digest.Create();long id=_options.OrdersPerTenant;var key=new{tenant=1L,id,take=2L};
#if POSTGRES
        var begin=connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
#else
        var begin=connection.BeginTransactionAsync(IsolationLevel.Serializable);
#endif
        if(!begin.IsCompleted)suspensions++;
        var transaction=await begin.ConfigureAwait(false);
        Order original;
        try
        {
        var find=connection.QuerySingleAsync<Order>(FindSql,key,transaction);if(!find.IsCompleted)suspensions++;

        try { original = await find.ConfigureAwait(false); }
        catch (InvalidOperationException error) { throw CorrectnessOracle.Failure("Expected exactly one full order from the fixed key lookup.", name, error); }
        if(original!=SeedOrder(id))throw CorrectnessOracle.Failure("Write target differs from fixed initial state.", name);
        HashOrder(ref digest,original);
        var changes=new{tenant=1L,id,note=name=="partial-update"?(string?)null:"perf-mutated",version=2L,expected=1L};
        var update=name=="stale-conflict"?connection.ExecuteAsync(CheckedSql,changes,transaction)
            :name=="partial-update"?connection.ExecuteAsync(PartialSql,changes,transaction):connection.ExecuteAsync(UpdateSql,changes,transaction);
        if(!update.IsCompleted)suspensions++;
        if(await update.ConfigureAwait(false)!=1)throw CorrectnessOracle.Failure("Mutation did not affect exactly one order.", name);
        if(name=="stale-conflict")
        {
            var caught=false;
            try
            {
                var stale=connection.ExecuteAsync(StaleSql,new{tenant=1L,id,note="stale-write",expected=1L},transaction);
                if(!stale.IsCompleted)suspensions++;
                if(await stale.ConfigureAwait(false)==0)throw new ApplicationConcurrencyConflictException();
            }
            catch(ApplicationConcurrencyConflictException){caught=true;}
            if(!caught)throw CorrectnessOracle.Failure("A stale optimistic write was accepted.", name);
            digest.Boolean(true);var rollback=transaction.RollbackAsync();if(!rollback.IsCompleted)suspensions++;await rollback.ConfigureAwait(false);
        }
        else
        {
            var check=connection.QuerySingleAsync<Order>(FindSql,key,transaction);if(!check.IsCompleted)suspensions++;
            Order changed;
        try { changed = await check.ConfigureAwait(false); }
        catch (InvalidOperationException error) { throw CorrectnessOracle.Failure("Expected exactly one full order from the fixed key lookup.", name, error); }
            var expected=name=="partial-update"?original with{Note=null}:original with{Note="perf-mutated",Version=2};
            if(changed!=expected)throw CorrectnessOracle.Failure("Partial mutation changed omitted fields or lost NULL.", name);
            HashOrder(ref digest,changed);
            if(name=="tx-rollback")
            {
                var rollback=transaction.RollbackAsync();if(!rollback.IsCompleted)suspensions++;await rollback.ConfigureAwait(false);
            }
            else
            {
                var restore=new{tenant=1L,id,note=original.Note,version=original.Version};
                var restoring=name=="partial-update"?connection.ExecuteAsync(PartialSql,restore,transaction):connection.ExecuteAsync(UpdateSql,restore,transaction);
                if(!restoring.IsCompleted)suspensions++;
                if(await restoring.ConfigureAwait(false)!=1)throw CorrectnessOracle.Failure("Restore did not affect exactly one order.", name);
                var commit=transaction.CommitAsync();if(!commit.IsCompleted)suspensions++;await commit.ConfigureAwait(false);
            }
        }
        }
        catch (Exception error) { CorrectnessOracle.ObserveContractFailure(error,name); throw; }
        finally
        {
            // Match the public Korda lifecycle: complete transaction disposal before
            // the independent persisted-state read on the retained connection.
            try
            {
                var retirement=transaction.DisposeAsync();if(!retirement.IsCompleted)suspensions++;
                await retirement.ConfigureAwait(false);
            }
            catch(Exception cleanup){CorrectnessOracle.ObserveCleanupFailure(cleanup,name);throw;}
        }
        var final=connection.QuerySingleAsync<Order>(FindSql,key);if(!final.IsCompleted)suspensions++;
        Order persisted;
        try { persisted = await final.ConfigureAwait(false); }
        catch (InvalidOperationException error) { throw CorrectnessOracle.Failure("Expected exactly one full order from the fixed key lookup.", name, error); }
        if(persisted!=original)throw CorrectnessOracle.Failure("Durable state was not restored after transaction.", name);
        HashOrder(ref digest,persisted);return new(name=="stale-conflict"?2:3,digest.Value,suspensions);
    }
    private sealed class ApplicationConcurrencyConflictException : Exception { }
}
