using System.Text.Json;
using DbPerf.Campaign;
using Korda.Database;
using Korda.Data;

internal sealed class KordaWorker : ICampaignWorker
{
    private readonly DataSource _source;
    private readonly Config _config;
    private readonly Workload? _workload;
    private readonly int _workerId;
    private DataSession? _session;

    private KordaWorker(DataSource source,Config config,Workload? workload,DataSession? session,int workerId)
    {_source=source;_config=config;_workload=workload;_session=session;_workerId=workerId;}

    internal static Options ToOptions(Config config)=>new()
    {
        Connection=config.Connection,Case="mixed",Mode=config.Mode,SessionMode=config.SessionMode,
        Concurrency=config.Concurrency,Warmup=config.Warmup,Rows=config.Rows,BatchRows=config.BatchRows,
        Tenants=config.Tenants,OrdersPerTenant=config.OrdersPerTenant,EmptyDriver=false,
        Initialize=config.Initialize,Verify=true,SelectedCases=config.Mix.Where(c=>c.Steps.Length==0).Select(c=>c.Name).ToArray(),
    };

    internal static async Task<KordaWorker> CreateAsync(DataSource source,Config config,int workerId)
    {
        var session=await source.OpenSessionAsync();
        Workload? workload=null;
        try
        {
            if(config.Mix.Any(c=>c.Steps.Length==0))workload=new(session,ToOptions(config));
            foreach(var query in config.Mix.Where(c=>c.Steps.Length==0))
            {
                var expected=workload!.Expected(query.Name);
                if(expected.Rows!=query.Expected.Rows||expected.Digest!=query.Expected.Digest)
                    throw new InvalidOperationException("Independent typed oracle differs: "+query.Name);
            }
            if(config.ExportSql is { } output)
            {
                var path=output+".worker-"+workerId+".json";
                using var file=File.Create(path);using var json=new Utf8JsonWriter(file,new(){Indented=true});
                json.WriteStartObject();json.WriteString("provider",config.Provider);json.WriteString("mode",config.Mode);
                json.WritePropertyName("queries");json.WriteStartArray();
                foreach(var pair in workload?.Sql??new Dictionary<string,string>())
                {json.WriteStartObject();json.WriteString("name",pair.Key);json.WriteString("sql",pair.Value);json.WriteEndObject();}
                json.WriteEndArray();json.WriteEndObject();
            }
            if(config.SessionMode=="persistent")return new(source,config,workload,session,workerId);
            // Return the setup lease before measurement: all N slots remain available.
            await session.DisposeAsync();
            return new(source,config,workload,null,workerId);
        }
        catch(Exception primary)
        {
            Exception? cleanup=null;
            try{workload?.Dispose();}catch(Exception error){cleanup=error;}
            try{await session.DisposeAsync();}catch(Exception error){cleanup=cleanup is null?error:new AggregateException(cleanup,error);}
            if(cleanup is not null)throw new AggregateException("Worker setup and retirement failed.",primary,cleanup);
            throw;
        }
    }

    public async ValueTask<Result> ExecuteAsync(Case query,long sequence)
    {
        try
        {
        Result result;
        if(_config.SessionMode=="persistent")
        {
            _session??=await _source.OpenSessionAsync().ConfigureAwait(false);
            try{result=await ExecuteOnSession(query,_session).ConfigureAwait(false);}
            catch(Exception primary)
            {
                // A failed API has already retired its operation. Reacquire for
                // a later request; there is no hidden retry of this request.
                var failedSession=_session;_session=null;
                try{await failedSession.DisposeAsync().ConfigureAwait(false);}
                catch(Exception cleanup){throw new AggregateException("Operation and session retirement failed.",primary,cleanup);}
                throw;
            }
        }
        else
        {
            var session=await _source.OpenSessionAsync().ConfigureAwait(false);
            try{result=await ExecuteOnSession(query,session).ConfigureAwait(false);}
            catch(Exception primary)
            {
                try{await session.DisposeAsync().ConfigureAwait(false);}
                catch(Exception cleanup){throw new AggregateException("Operation and leased-session retirement failed.",primary,cleanup);}
                throw;
            }
            await session.DisposeAsync().ConfigureAwait(false);
        }
        return result;
        }
        catch(DatabaseException error) when(error.Kind is DatabaseErrorKind.Provider or DatabaseErrorKind.Constraint or DatabaseErrorKind.ResourceLimit)
        {
            throw new CampaignException(error.Kind==DatabaseErrorKind.ResourceLimit?"resource_limit":"provider_error",error.ProviderCode,error);
        }
    }

    private async ValueTask<Result> ExecuteOnSession(Case query,DataSession session)
    {
        if(query.Steps.Length!=0)return await TransactionWorkload.ExecuteAsync(session,query.Name,_workerId).ConfigureAwait(false);
        var result=await _workload!.Execute(query.Name,session).ConfigureAwait(false);
        return new(result.Rows,result.Digest,result.Suspensions);
    }

    public async ValueTask DisposeAsync()
    {_workload?.Dispose();if(_session is not null){var session=_session;_session=null;await session.DisposeAsync();}}
}
