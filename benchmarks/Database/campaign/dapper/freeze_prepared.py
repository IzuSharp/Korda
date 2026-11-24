#!/usr/bin/env python3
"""Emit ordinary C# input with constant SQL for public Dapper.AOT CacheCommand.

The runtime validates these constants against its parsed contract before work.
No generated interceptor output is edited. Dynamic call sites remain separate.
"""

import argparse
import hashlib
import json
from pathlib import Path


def rewrite(sql, provider, count):
    out = []; seen = set(); position = 0; index = 0; quote = None
    while index < len(sql):
        char = sql[index]
        following = sql[index + 1] if index + 1 < len(sql) else ""
        if quote:
            out.append(char)
            if char == quote:
                if following == quote:
                    out.append(following); index += 1
                else:
                    quote = None
        elif char in "'\"":
            quote = char; out.append(char)
        elif char == "-" and following == "-":
            end = sql.find("\n", index)
            if end < 0: end = len(sql)
            out.append(sql[index:end]); index = end - 1
        elif char == "/" and following == "*":
            end = sql.find("*/", index + 2)
            if end < 0: raise ValueError("Unterminated SQL comment")
            out.append(sql[index:end + 2]); index = end + 1
        elif char == "?" and provider == "sqlite":
            if following.isdigit(): raise ValueError("Indexed SQLite tokens are outside the contract")
            position += 1; seen.add(position); out.append("@p" + str(position))
        elif char == "$" and provider == "postgresql":
            end = index + 1
            while end < len(sql) and sql[end].isdigit(): end += 1
            if end == index + 1: raise ValueError("Dollar quoting is outside this finite contract")
            number = int(sql[index + 1:end]); seen.add(number); out.append("@p" + str(number)); index = end - 1
        else:
            out.append(char)
        index += 1
    if quote or seen != set(range(1, count + 1)):
        raise ValueError("SQL quote/parameter structure differs from the contract")
    return "".join(out)


def cs(value):
    return json.dumps(value, ensure_ascii=False)


def generate(raw):
    contract = json.loads(raw); digest = hashlib.sha256(raw).hexdigest()
    reads = [case for case in contract["cases"] if not case.get("steps")]
    if len(reads) != 32: raise ValueError("Review the generated adapter when the 32-read contract changes")
    result = ["// Produced by freeze_prepared.py from the frozen campaign contract; ordinary compiler input.",
              "using System.Data.Common;", "using Dapper;", "using DbPerf.Campaign;", "#if POSTGRESQL",
              "using ProviderConnection = Npgsql.NpgsqlConnection;", "#else",
              "using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;", "#endif", "",
              "namespace DbPerf.IndependentOrm;", ""]
    types = {"scalar-int64": ("long", "HashInteger"), "text": ("TextRow", "HashText"),
             "money": ("MoneyRow", "HashMoney"), "json": ("JsonRow", "HashJson"),
             "wide": ("WideRow", "HashWide"), "join": ("JoinRow", "HashJoin"),
             "customer-total": ("CustomerTotalRow", "HashCustomerTotal"),
             "product-total": ("ProductTotalRow", "HashProductTotal"), "rank": ("RankRow", "HashRank"),
             "running": ("RunningRow", "HashRunning"), "detail": ("DetailRow", "HashDetail")}
    for provider in ["sqlite", "postgresql"]:
        result += ["#if " + ("SQLITE" if provider == "sqlite" else "POSTGRESQL"),
                   "internal static class PreparedSql", "{", f"    internal const string ContractSha256 = {cs(digest)};"]
        for query in reads:
            sql = rewrite(query["sql"][provider], provider, len(query["parameters"]))
            result.append(f"    internal const string {query['id']} = {cs(sql)};")
        state = {}
        for query in contract["cases"]:
            for step in query.get("steps", []):
                if step["kind"] not in ("execute", "select"): continue
                key = (step["kind"], tuple(p["type"] for p in step["parameters"]))
                name = {("select", ("int64",)): "key", ("execute", ("int64", "int64", "text", "int64")): "set",
                        ("execute", ("text", "int64")): "note", ("execute", ("int64", "int64", "int64")): "conflict"}[key]
                sql = rewrite(step["sql"][provider], provider, len(step["parameters"]))
                if name in state and state[name] != sql: raise ValueError("State shape has multiple SQL texts")
                state[name] = sql
        if set(state) != {"key", "set", "note", "conflict"}: raise ValueError("State SQL shapes changed")
        for name, sql in state.items(): result.append(f"    internal const string State{name.title()} = {cs(sql)};")
        result += ["", "    internal static string ForRead(string id) => id switch", "    {"]
        result += [f"        {cs(query['id'])} => {query['id']}," for query in reads]
        result += ["        _ => throw new NotSupportedException(\"No frozen prepared SQL for \" + id),", "    };",
                   "    internal static string ForState(string shape) => shape switch", "    {"]
        result += [f"        {cs(name)} => State{name.title()}," for name in state]
        result += ["        _ => throw new NotSupportedException(\"No frozen prepared state SQL for \" + shape),", "    };", "}", "",
                   "internal sealed partial class DapperWorker", "{", "    [CacheCommand]",
                   "    private ValueTask<Result> ExecuteReadPrepared(ProviderConnection connection, ReadPlan plan) => plan.Case.Id switch", "    {"]
        for query in reads:
            shape = query["materialization"]; name = query["id"]
            if shape == "related":
                expression = f"ReadRelated(connection.QueryUnbufferedAsync<RelatedRow>(PreparedSql.{name}, plan.Related!, commandTimeout: 5), plan.Case)"
            else:
                row, hash_method = (("long?", "HashNullableCents") if provider == "sqlite" else ("decimal?", "HashNullableMoney")) if shape == "nullable-money" else types[shape]
                args = "Literal" if any(p["type"] == "text" for p in query["parameters"]) else "Common"
                expression = f"Read(connection.QueryUnbufferedAsync<{row}>(PreparedSql.{name}, plan.{args}!, commandTimeout: 5), {hash_method})"
            result.append(f"        {cs(name)} => {expression},")
        result += ["        _ => throw new NotSupportedException(\"Prepared read was not frozen into this executable.\"),", "    };", "",
                   "    [CacheCommand]", "    private static IAsyncEnumerable<StateRow> QueryStatePrepared(ProviderConnection connection, DbTransaction? transaction, StateStep step) =>",
                   "        connection.QueryUnbufferedAsync<StateRow>(PreparedSql.StateKey, step.Key!, transaction: transaction, commandTimeout: 5);", "",
                   "    [CacheCommand]", "    private static Task<int> ExecuteStateCommandPrepared(ProviderConnection connection, DbTransaction? transaction, StateStep step) => step.Shape switch", "    {"]
        for name in ["set", "note", "conflict"]:
            result.append(f"        {cs(name)} => connection.ExecuteAsync(PreparedSql.State{name.title()}, step.{name.title()}!, transaction: transaction, commandTimeout: 5),")
        result += ["        _ => throw new NotSupportedException(\"Prepared state command was not frozen into this executable.\"),", "    };", "}", "#endif", ""]
    return "\n".join(result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(); raw = args.manifest.read_bytes(); source = generate(raw)
    if args.check:
        if args.output.read_text() != source: raise ValueError("Prepared C# differs from the frozen contract; regenerate explicitly before building")
    else:
        args.output.write_text(source)
    print(json.dumps({"status": "matched" if args.check else "generated", "contract_sha256": hashlib.sha256(raw).hexdigest(),
                      "source_sha256": hashlib.sha256(source.encode()).hexdigest(), "prepared_call_sites_per_provider": 36}))


if __name__ == "__main__": main()
