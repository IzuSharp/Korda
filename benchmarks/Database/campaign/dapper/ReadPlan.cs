using System.Globalization;
using System.Text;
using DbPerf.Campaign;

namespace DbPerf.IndependentOrm;

internal sealed class ReadPlan
{
    internal Case Case { get; }
    internal string CommandSql { get; }
    internal string[] CommandFragments { get; }
    internal long[] Integers { get; }
    internal CommonTypedArgs? Common { get; }
    internal LiteralTypedArgs? Literal { get; }
    internal RelatedParameters96? Related { get; }
    internal bool HasText { get; }

    internal ReadPlan(Case query, string provider, bool prepared)
    {
        Case = query;
        if (query.Parameters.Length is < 1 or > 96)
            throw new NotSupportedException($"{query.Id}: unsupported parameter count {query.Parameters.Length}.");
        (CommandSql, CommandFragments) = Rewrite(query.Sql, query.Fragments, provider, query.Parameters.Length);
        if (prepared && CommandSql != PreparedSql.ForRead(query.Id))
            throw new InvalidOperationException("Prepared constant SQL differs from the parsed named-parameter contract.");
        Integers = new long[query.Parameters.Length];
        for (var index = 0; index < query.Parameters.Length; index++)
        {
            var value = query.Parameters[index];
            if (value.Runtime is not null) throw new NotSupportedException("Read parameters must be frozen values.");
            if (value.Type is "int64" or "int32") Integers[index] = value.Integer;
            else if (value.Type == "text") HasText = true;
            else throw new NotSupportedException($"{query.Id}: unsupported parameter type {value.Type}.");
        }

        if (query.Materialization == "related")
        {
            if (HasText || Integers.Length != 96 || query.GroupCount != 32 || query.ParentIds.Length != 32)
                throw new NotSupportedException("The current relation adapter is bounded to the declared 32-key/96-parameter contract.");
            for (var parent = 0; parent < 32; parent++)
            {
                var offset = parent * 3;
                if (query.Parameters[offset].Type != "int32" || Integers[offset] != parent ||
                    query.Parameters[offset + 1].Type != "int64" || query.Parameters[offset + 2].Type != "int64" ||
                    Integers[offset + 2] != query.ParentIds[parent])
                    throw new InvalidOperationException("The related parameter types, parent ordinal, or composite keys differ from the contract.");
            }
            if (prepared) Related = new(Integers);
        }
        else if (HasText)
        {
            if (query.Materialization != "scalar-int64" || query.Parameters.Length != 3 ||
                query.Parameters[0].Type != "int64" || query.Parameters[1].Type != "text" || query.Parameters[2].Type != "int64")
                throw new NotSupportedException($"{query.Id}: only the declared (Int64, text, Int64) read parameter shape is supported.");
            if (prepared) Literal = NewLiteral();
        }
        else
        {
            if (Integers.Length > 8) throw new NotSupportedException($"{query.Id}: ordinary reads support p1..p8.");
            if (query.Parameters.Any(parameter => parameter.Type != "int64"))
                throw new NotSupportedException("Ordinary integer read arguments require Int64 storage.");
            if (prepared) Common = new(Integers);
        }

        ValidateColumns(query);
    }

    internal string Sql(bool prepared) => prepared ? CommandSql : string.Concat(CommandFragments);
    internal LiteralTypedArgs NewLiteral() => new(Integers[0], Case.Parameters[1].Text
        ?? throw new InvalidOperationException("The pattern must not be null."), Integers[2]);

    internal static (string Sql, string[] Fragments) Rewrite(string sql, string[] fragments, string provider, int count)
    {
        if (string.Concat(fragments) != sql)
            throw new InvalidOperationException("Fragments do not reconstruct the frozen SQL.");
        var scanner = new PositionalParameters(provider, count);
        var rewritten = fragments.Select(scanner.Rewrite).ToArray();
        scanner.Complete();
        return (string.Concat(rewritten), rewritten);
    }

    private static void ValidateColumns(Case query)
    {
        static Column C(string name, string type, bool nullable = false) => new(name, type, nullable);
        Column[] expected = query.Materialization switch
        {
            "scalar-int64" => [C("value", "int64")],
            "nullable-money" => [C("value", "nullable-money", true)],
            "text" => [C("Id", "int64"), C("Note", "string", true)],
            "money" => [C("Id", "int64"), C("Total", "money")],
            "json" => [C("Id", "int64"), C("Settings", "settings")],
            "wide" => [C("TenantId", "int64"), C("Id", "int64"), C("CreatedAt", "int64"),
                C("CustomerId", "nullable-int64", true), C("Total", "money"), C("Note", "string", true),
                C("Version", "int64"), C("Settings", "settings"), C("InternalMemo", "string")],
            "join" => [C("Id", "int64"), C("Name", "string", true)],
            "customer-total" => [C("CustomerId", "nullable-int64", true), C("Count", "int64"), C("Total", "nullable-money", true)],
            "product-total" => [C("Product", "string"), C("Quantity", "nullable-int64", true)],
            "rank" => [C("Id", "int64"), C("Rank", "int64")],
            "running" => [C("Id", "int64"), C("Quantity", "nullable-int64", true)],
            "detail" => [C("OrderId", "int64"), C("Name", "string"), C("Product", "string"), C("Quantity", "int64")],
            "related" => [C("Id", "int64"), C("TenantId", "int64"), C("OrderId", "int64"),
                C("Product", "string"), C("Quantity", "int64"), C("__korda_parent_index", "int32")],
            _ => throw new NotSupportedException($"{query.Id}: materialization {query.Materialization} has no explicit generated Dapper call site."),
        };
        if (!query.Columns.SequenceEqual(expected))
            throw new InvalidOperationException($"{query.Id}: declared columns differ from the statically generated mapper.");
    }

    // This scanner adapts positional engine SQL to Dapper's named parameter API during setup.
    // It retains the clause boundaries so dynamic mode still constructs a SQL string per operation.
    // The accepted shared contract uses standard quoted strings/identifiers and positional tokens.
    private sealed class PositionalParameters
    {
        private readonly string _provider;
        private readonly bool[] _seen;
        private int _sqlitePosition, _blockDepth;
        private char _quote;
        private bool _lineComment;

        internal PositionalParameters(string provider, int count)
        {
            if (provider is not ("sqlite" or "postgresql")) throw new ArgumentException("Unknown provider.", nameof(provider));
            _provider = provider; _seen = new bool[count];
        }

        internal string Rewrite(string fragment)
        {
            var result = new StringBuilder(fragment.Length + 32);
            for (var index = 0; index < fragment.Length; index++)
            {
                var value = fragment[index];
                var next = index + 1 < fragment.Length ? fragment[index + 1] : '\0';
                if (_lineComment)
                {
                    result.Append(value);
                    if (value is '\n' or '\r') _lineComment = false;
                    continue;
                }
                if (_blockDepth != 0)
                {
                    result.Append(value);
                    if (value == '/' && next == '*') { result.Append(next); index++; _blockDepth++; }
                    else if (value == '*' && next == '/') { result.Append(next); index++; _blockDepth--; }
                    continue;
                }
                if (_quote != '\0')
                {
                    result.Append(value);
                    if (value == _quote)
                    {
                        if (next == _quote) { result.Append(next); index++; }
                        else _quote = '\0';
                    }
                    continue;
                }
                if (value is '\'' or '"') { _quote = value; result.Append(value); continue; }
                if (value == '-' && next == '-') { _lineComment = true; result.Append(value).Append(next); index++; continue; }
                if (value == '/' && next == '*') { _blockDepth = 1; result.Append(value).Append(next); index++; continue; }

                var parameter = 0;
                var isParameter = false;
                if (_provider == "sqlite" && value == '?')
                {
                    if (char.IsAsciiDigit(next)) throw new NotSupportedException("Only anonymous SQLite positional tokens are in this contract.");
                    parameter = ++_sqlitePosition;
                    isParameter = true;
                }
                else if (_provider == "postgresql" && value == '$')
                {
                    if (!char.IsAsciiDigit(next)) throw new NotSupportedException("Dollar-quoted SQL is not in this finite contract.");
                    while (index + 1 < fragment.Length && char.IsAsciiDigit(fragment[index + 1]))
                        parameter = checked(parameter * 10 + fragment[++index] - '0');
                    isParameter = true;
                }
                if (isParameter)
                {
                    if (parameter < 1 || parameter > _seen.Length) throw new InvalidOperationException("SQL parameter ordinal exceeds the declared binding list.");
                    _seen[parameter - 1] = true;
                    result.Append("@p").Append(parameter.ToString(CultureInfo.InvariantCulture));
                }
                else result.Append(value);
            }
            return result.ToString();
        }

        internal void Complete()
        {
            if (_quote != '\0' || _blockDepth != 0 || _seen.Any(seen => !seen))
                throw new InvalidOperationException("The SQL lexical state or parameter list differs from the frozen contract.");
        }
    }
}
