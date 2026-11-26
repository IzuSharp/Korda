using DbPerf.Campaign;

namespace DbPerf.IndependentOrm;

public sealed record StateRow(long Id, long Counter, long Version, string? Note);
public sealed record StateKeyArgs(long p1);
public sealed record StateSetArgs(long p1, long p2, string? p3, long p4);
public sealed record StateNoteArgs(string? p1, long p2);
public sealed record StateConflictArgs(long p1, long p2, long p3);

internal sealed class StatePlan
{
    internal Case Case { get; }
    internal long WorkerKey { get; }
    internal StateStep[] Steps { get; }

    internal StatePlan(Case query, string provider, bool prepared, int workerId)
    {
        if (query.Materialization != "state" || query.Steps.Length is < 1 or > 6)
            throw new NotSupportedException("Only the frozen finite state-operation contract is implemented.");
        Column[] columns = [new("Id", "int64", false), new("Counter", "int64", false),
            new("Version", "int64", false), new("Note", "string", true)];
        if (!query.Columns.SequenceEqual(columns))
            throw new InvalidOperationException("State columns differ from the concrete generated mapper.");
        Case = query;
        WorkerKey = checked(workerId + 1L);
        Steps = query.Steps.Select(step => new StateStep(step, provider, prepared, WorkerKey)).ToArray();
    }
}

internal sealed class StateStep
{
    internal Step Contract { get; }
    internal string CommandSql { get; }
    internal string[] CommandFragments { get; }
    internal Parameter[] BoundParameters { get; }
    internal string Shape { get; }
    internal StateKeyArgs? Key { get; }
    internal StateSetArgs? Set { get; }
    internal StateNoteArgs? Note { get; }
    internal StateConflictArgs? Conflict { get; }

    internal StateStep(Step step, string provider, bool prepared, long workerKey)
    {
        Contract = step;
        (CommandSql, CommandFragments) = ReadPlan.Rewrite(step.Sql, step.Fragments, provider, step.Parameters.Length);
        BoundParameters = step.Parameters.Select(parameter => parameter.Runtime switch
        {
            null => parameter,
            "worker_id_plus_one" when parameter.Type == "int64" => parameter with { Integer = workerKey },
            _ => throw new NotSupportedException("Unknown state parameter runtime binding."),
        }).ToArray();
        var types = string.Join(',', BoundParameters.Select(parameter => parameter.Type));
        Shape = (step.Kind, types) switch
        {
            ("begin" or "commit" or "rollback", "") => "control",
            ("select", "int64") when step.ExpectedCounter is not null && step.ExpectedVersion is not null => "key",
            ("execute", "int64,int64,text,int64") when step.ExpectedAffected == 1 => "set",
            ("execute", "text,int64") when step.ExpectedAffected == 1 => "note",
            ("execute", "int64,int64,int64") when step.ExpectedAffected == 0 => "conflict",
            _ => throw new NotSupportedException("No concrete generated Dapper binding for state step " + step.Kind + " (" + types + ")."),
        };
        if (prepared)
        {
            if (Shape != "control" && CommandSql != PreparedSql.ForState(Shape))
                throw new InvalidOperationException("Prepared state SQL differs from the parsed contract.");
            if (Shape == "key") Key = NewKey();
            else if (Shape == "set") Set = NewSet();
            else if (Shape == "note") Note = NewNote();
            else if (Shape == "conflict") Conflict = NewConflict();
        }
    }

    internal string Sql(bool prepared) => prepared ? CommandSql : string.Concat(CommandFragments);
    internal StateKeyArgs NewKey() => new(BoundParameters[0].Integer);
    internal StateSetArgs NewSet() => new(BoundParameters[0].Integer, BoundParameters[1].Integer,
        BoundParameters[2].Text, BoundParameters[3].Integer);
    internal StateNoteArgs NewNote() => new(BoundParameters[0].Text, BoundParameters[1].Integer);
    internal StateConflictArgs NewConflict() => new(BoundParameters[0].Integer, BoundParameters[1].Integer, BoundParameters[2].Integer);
}
