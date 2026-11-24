// Produced by freeze_prepared.py from the frozen campaign contract; ordinary compiler input.
using System.Data.Common;
using Dapper;
using DbPerf.Campaign;
#if POSTGRESQL
using ProviderConnection = Npgsql.NpgsqlConnection;
#else
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace DbPerf.IndependentOrm;

#if SQLITE
internal static class PreparedSql
{
    internal const string ContractSha256 = "f08b4822818c52ddad1bdfaab3a75f3784a6f35c10bf3965b043771cd0f4e05f";
    internal const string R01 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R02 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Note\" AS \"Note\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R03 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Total\" AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R04 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Settings\" AS \"Settings\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R05 = "SELECT \"orders\".\"TenantId\" AS \"TenantId\", \"orders\".\"Id\" AS \"Id\", \"orders\".\"CreatedAt\" AS \"CreatedAt\", \"orders\".\"CustomerId\" AS \"CustomerId\", \"orders\".\"Total\" AS \"Total\", \"orders\".\"Note\" AS \"Note\", \"orders\".\"Version\" AS \"Version\", \"orders\".\"Settings\" AS \"Settings\", \"orders\".\"InternalMemo\" AS \"InternalMemo\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R06 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"CustomerId\" = @p2 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R07 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" >= @p2 AND \"orders\".\"CreatedAt\" < @p3) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    internal const string R08 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" LEFT JOIN (SELECT \"Email\", \"Name\", \"Id\", 1 AS \"_nw_match\" FROM \"customers\") AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R09 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R10 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" IS NULL ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R11 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" LIKE @p2 ESCAPE '\\' ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R12 = "SELECT \"orders\".\"CustomerId\" AS \"CustomerId\", COUNT(*) AS \"Count\", SUM(\"orders\".\"Total\") AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 GROUP BY \"orders\".\"CustomerId\" HAVING COUNT(*) > @p2 ORDER BY \"orders\".\"CustomerId\" ASC NULLS LAST";
    internal const string R13 = "SELECT \"order_lines\".\"Product\" AS \"Product\", SUM(\"order_lines\".\"Quantity\") AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 GROUP BY \"order_lines\".\"Product\" HAVING SUM(\"order_lines\".\"Quantity\") > @p2 ORDER BY \"order_lines\".\"Product\" ASC NULLS LAST";
    internal const string R14 = "SELECT SUM(\"orders\".\"Total\") AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1";
    internal const string R15 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" > @p2 OR (\"orders\".\"CreatedAt\" = @p3 AND (\"orders\".\"TenantId\" > @p4 OR (\"orders\".\"TenantId\" = @p5 AND \"orders\".\"Id\" > @p6)))) ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p7";
    internal const string R16 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2 OFFSET @p3";
    internal const string R17 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE EXISTS(SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" >= @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R18 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE \"customers\".\"Id\" IN (SELECT \"orders\".\"CustomerId\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R19 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R20 = "SELECT \"orders\".\"Id\" AS \"Id\", (ROW_NUMBER() OVER ( PARTITION BY \"orders\".\"CustomerId\" ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST )) AS \"Rank\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R21 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION ALL SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R22 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R23 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 INTERSECT SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R24 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 EXCEPT SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R25 = "SELECT \"order_lines\".\"Id\" AS \"Id\", (SUM(\"order_lines\".\"Quantity\") OVER (  ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW )) AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R26 = "SELECT \"orders\".\"Id\" AS \"OrderId\", \"customer\".\"Name\" AS \"Name\", \"line\".\"Product\" AS \"Product\", \"line\".\"Quantity\" AS \"Quantity\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" INNER JOIN \"order_lines\" AS \"line\" ON \"orders\".\"TenantId\" = \"line\".\"TenantId\" AND \"orders\".\"Id\" = \"line\".\"OrderId\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST, \"line\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R27 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (NOT \"orders\".\"Id\" IN (SELECT \"order_lines\".\"OrderId\" AS \"value\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p2 AND \"order_lines\".\"Quantity\" >= @p3)) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    internal const string R28 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CustomerId\" ASC NULLS FIRST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R29 = "SELECT \"Id\",\"Note\" FROM orders WHERE \"TenantId\"=@p1 ORDER BY \"Id\" LIMIT @p2";
    internal const string R30 = "SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p1 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p2 AND \"order_lines\".\"OrderId\" = @p3 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p4 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p5 AND \"order_lines\".\"OrderId\" = @p6 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p7 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p8 AND \"order_lines\".\"OrderId\" = @p9 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p10 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p11 AND \"order_lines\".\"OrderId\" = @p12 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p13 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p14 AND \"order_lines\".\"OrderId\" = @p15 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p16 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p17 AND \"order_lines\".\"OrderId\" = @p18 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p19 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p20 AND \"order_lines\".\"OrderId\" = @p21 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p22 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p23 AND \"order_lines\".\"OrderId\" = @p24 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p25 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p26 AND \"order_lines\".\"OrderId\" = @p27 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p28 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p29 AND \"order_lines\".\"OrderId\" = @p30 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p31 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p32 AND \"order_lines\".\"OrderId\" = @p33 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p34 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p35 AND \"order_lines\".\"OrderId\" = @p36 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p37 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p38 AND \"order_lines\".\"OrderId\" = @p39 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p40 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p41 AND \"order_lines\".\"OrderId\" = @p42 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p43 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p44 AND \"order_lines\".\"OrderId\" = @p45 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p46 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p47 AND \"order_lines\".\"OrderId\" = @p48 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p49 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p50 AND \"order_lines\".\"OrderId\" = @p51 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p52 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p53 AND \"order_lines\".\"OrderId\" = @p54 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p55 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p56 AND \"order_lines\".\"OrderId\" = @p57 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p58 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p59 AND \"order_lines\".\"OrderId\" = @p60 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p61 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p62 AND \"order_lines\".\"OrderId\" = @p63 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p64 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p65 AND \"order_lines\".\"OrderId\" = @p66 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p67 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p68 AND \"order_lines\".\"OrderId\" = @p69 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p70 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p71 AND \"order_lines\".\"OrderId\" = @p72 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p73 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p74 AND \"order_lines\".\"OrderId\" = @p75 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p76 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p77 AND \"order_lines\".\"OrderId\" = @p78 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p79 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p80 AND \"order_lines\".\"OrderId\" = @p81 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p82 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p83 AND \"order_lines\".\"OrderId\" = @p84 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p85 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p86 AND \"order_lines\".\"OrderId\" = @p87 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p88 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p89 AND \"order_lines\".\"OrderId\" = @p90 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p91 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p92 AND \"order_lines\".\"OrderId\" = @p93 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p94 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p95 AND \"order_lines\".\"OrderId\" = @p96";
    internal const string R31 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R32 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string StateSet = "UPDATE \"perf_state\" SET \"Counter\" = @p1, \"Version\" = @p2, \"Note\" = @p3 WHERE \"Id\" = @p4";
    internal const string StateKey = "SELECT \"Id\", \"Counter\", \"Version\", \"Note\" FROM \"perf_state\" WHERE \"Id\" = @p1";
    internal const string StateNote = "UPDATE \"perf_state\" SET \"Note\" = @p1 WHERE \"Id\" = @p2";
    internal const string StateConflict = "UPDATE \"perf_state\" SET \"Counter\" = @p1 WHERE \"Id\" = @p2 AND \"Version\" = @p3";

    internal static string ForRead(string id) => id switch
    {
        "R01" => R01,
        "R02" => R02,
        "R03" => R03,
        "R04" => R04,
        "R05" => R05,
        "R06" => R06,
        "R07" => R07,
        "R08" => R08,
        "R09" => R09,
        "R10" => R10,
        "R11" => R11,
        "R12" => R12,
        "R13" => R13,
        "R14" => R14,
        "R15" => R15,
        "R16" => R16,
        "R17" => R17,
        "R18" => R18,
        "R19" => R19,
        "R20" => R20,
        "R21" => R21,
        "R22" => R22,
        "R23" => R23,
        "R24" => R24,
        "R25" => R25,
        "R26" => R26,
        "R27" => R27,
        "R28" => R28,
        "R29" => R29,
        "R30" => R30,
        "R31" => R31,
        "R32" => R32,
        _ => throw new NotSupportedException("No frozen prepared SQL for " + id),
    };
    internal static string ForState(string shape) => shape switch
    {
        "set" => StateSet,
        "key" => StateKey,
        "note" => StateNote,
        "conflict" => StateConflict,
        _ => throw new NotSupportedException("No frozen prepared state SQL for " + shape),
    };
}

internal sealed partial class DapperWorker
{
    [CacheCommand]
    private ValueTask<Result> ExecuteReadPrepared(ProviderConnection connection, ReadPlan plan) => plan.Case.Id switch
    {
        "R01" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R01, plan.Common!, commandTimeout: 5), HashInteger),
        "R02" => Read(connection.QueryUnbufferedAsync<TextRow>(PreparedSql.R02, plan.Common!, commandTimeout: 5), HashText),
        "R03" => Read(connection.QueryUnbufferedAsync<MoneyRow>(PreparedSql.R03, plan.Common!, commandTimeout: 5), HashMoney),
        "R04" => Read(connection.QueryUnbufferedAsync<JsonRow>(PreparedSql.R04, plan.Common!, commandTimeout: 5), HashJson),
        "R05" => Read(connection.QueryUnbufferedAsync<WideRow>(PreparedSql.R05, plan.Common!, commandTimeout: 5), HashWide),
        "R06" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R06, plan.Common!, commandTimeout: 5), HashInteger),
        "R07" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R07, plan.Common!, commandTimeout: 5), HashInteger),
        "R08" => Read(connection.QueryUnbufferedAsync<JoinRow>(PreparedSql.R08, plan.Common!, commandTimeout: 5), HashJoin),
        "R09" => Read(connection.QueryUnbufferedAsync<JoinRow>(PreparedSql.R09, plan.Common!, commandTimeout: 5), HashJoin),
        "R10" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R10, plan.Common!, commandTimeout: 5), HashInteger),
        "R11" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R11, plan.Literal!, commandTimeout: 5), HashInteger),
        "R12" => Read(connection.QueryUnbufferedAsync<CustomerTotalRow>(PreparedSql.R12, plan.Common!, commandTimeout: 5), HashCustomerTotal),
        "R13" => Read(connection.QueryUnbufferedAsync<ProductTotalRow>(PreparedSql.R13, plan.Common!, commandTimeout: 5), HashProductTotal),
        "R14" => Read(connection.QueryUnbufferedAsync<long?>(PreparedSql.R14, plan.Common!, commandTimeout: 5), HashNullableCents),
        "R15" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R15, plan.Common!, commandTimeout: 5), HashInteger),
        "R16" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R16, plan.Common!, commandTimeout: 5), HashInteger),
        "R17" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R17, plan.Common!, commandTimeout: 5), HashInteger),
        "R18" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R18, plan.Common!, commandTimeout: 5), HashInteger),
        "R19" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R19, plan.Common!, commandTimeout: 5), HashInteger),
        "R20" => Read(connection.QueryUnbufferedAsync<RankRow>(PreparedSql.R20, plan.Common!, commandTimeout: 5), HashRank),
        "R21" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R21, plan.Common!, commandTimeout: 5), HashInteger),
        "R22" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R22, plan.Common!, commandTimeout: 5), HashInteger),
        "R23" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R23, plan.Common!, commandTimeout: 5), HashInteger),
        "R24" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R24, plan.Common!, commandTimeout: 5), HashInteger),
        "R25" => Read(connection.QueryUnbufferedAsync<RunningRow>(PreparedSql.R25, plan.Common!, commandTimeout: 5), HashRunning),
        "R26" => Read(connection.QueryUnbufferedAsync<DetailRow>(PreparedSql.R26, plan.Common!, commandTimeout: 5), HashDetail),
        "R27" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R27, plan.Common!, commandTimeout: 5), HashInteger),
        "R28" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R28, plan.Common!, commandTimeout: 5), HashInteger),
        "R29" => Read(connection.QueryUnbufferedAsync<TextRow>(PreparedSql.R29, plan.Common!, commandTimeout: 5), HashText),
        "R30" => ReadRelated(connection.QueryUnbufferedAsync<RelatedRow>(PreparedSql.R30, plan.Related!, commandTimeout: 5), plan.Case),
        "R31" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R31, plan.Common!, commandTimeout: 5), HashInteger),
        "R32" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R32, plan.Common!, commandTimeout: 5), HashInteger),
        _ => throw new NotSupportedException("Prepared read was not frozen into this executable."),
    };

    [CacheCommand]
    private static IAsyncEnumerable<StateRow> QueryStatePrepared(ProviderConnection connection, DbTransaction? transaction, StateStep step) =>
        connection.QueryUnbufferedAsync<StateRow>(PreparedSql.StateKey, step.Key!, transaction: transaction, commandTimeout: 5);

    [CacheCommand]
    private static Task<int> ExecuteStateCommandPrepared(ProviderConnection connection, DbTransaction? transaction, StateStep step) => step.Shape switch
    {
        "set" => connection.ExecuteAsync(PreparedSql.StateSet, step.Set!, transaction: transaction, commandTimeout: 5),
        "note" => connection.ExecuteAsync(PreparedSql.StateNote, step.Note!, transaction: transaction, commandTimeout: 5),
        "conflict" => connection.ExecuteAsync(PreparedSql.StateConflict, step.Conflict!, transaction: transaction, commandTimeout: 5),
        _ => throw new NotSupportedException("Prepared state command was not frozen into this executable."),
    };
}
#endif

#if POSTGRESQL
internal static class PreparedSql
{
    internal const string ContractSha256 = "f08b4822818c52ddad1bdfaab3a75f3784a6f35c10bf3965b043771cd0f4e05f";
    internal const string R01 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R02 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Note\" AS \"Note\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R03 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Total\" AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R04 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Settings\" AS \"Settings\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R05 = "SELECT \"orders\".\"TenantId\" AS \"TenantId\", \"orders\".\"Id\" AS \"Id\", \"orders\".\"CreatedAt\" AS \"CreatedAt\", \"orders\".\"CustomerId\" AS \"CustomerId\", \"orders\".\"Total\" AS \"Total\", \"orders\".\"Note\" AS \"Note\", \"orders\".\"Version\" AS \"Version\", \"orders\".\"Settings\" AS \"Settings\", \"orders\".\"InternalMemo\" AS \"InternalMemo\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R06 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"CustomerId\" = @p2 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R07 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" >= @p2 AND \"orders\".\"CreatedAt\" < @p3) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    internal const string R08 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" LEFT JOIN (SELECT \"Email\", \"Name\", \"Id\", 1 AS \"_nw_match\" FROM \"customers\") AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R09 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R10 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" IS NULL ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R11 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" LIKE @p2 ESCAPE E'\\\\' ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R12 = "SELECT \"orders\".\"CustomerId\" AS \"CustomerId\", COUNT(*) AS \"Count\", SUM(\"orders\".\"Total\") AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 GROUP BY \"orders\".\"CustomerId\" HAVING COUNT(*) > @p2 ORDER BY \"orders\".\"CustomerId\" ASC NULLS LAST";
    internal const string R13 = "SELECT \"order_lines\".\"Product\" AS \"Product\", CAST(SUM(\"order_lines\".\"Quantity\") AS bigint) AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 GROUP BY \"order_lines\".\"Product\" HAVING CAST(SUM(\"order_lines\".\"Quantity\") AS bigint) > @p2 ORDER BY \"order_lines\".\"Product\" ASC NULLS LAST";
    internal const string R14 = "SELECT SUM(\"orders\".\"Total\") AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1";
    internal const string R15 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" > @p2 OR (\"orders\".\"CreatedAt\" = @p3 AND (\"orders\".\"TenantId\" > @p4 OR (\"orders\".\"TenantId\" = @p5 AND \"orders\".\"Id\" > @p6)))) ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p7";
    internal const string R16 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2 OFFSET @p3";
    internal const string R17 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE EXISTS(SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" >= @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R18 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE \"customers\".\"Id\" IN (SELECT \"orders\".\"CustomerId\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R19 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST LIMIT @p3";
    internal const string R20 = "SELECT \"orders\".\"Id\" AS \"Id\", (ROW_NUMBER() OVER ( PARTITION BY \"orders\".\"CustomerId\" ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST )) AS \"Rank\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R21 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION ALL (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R22 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R23 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 INTERSECT (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R24 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 EXCEPT (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    internal const string R25 = "SELECT \"order_lines\".\"Id\" AS \"Id\", CAST(((SUM(\"order_lines\".\"Quantity\") OVER (  ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW ))) AS bigint) AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R26 = "SELECT \"orders\".\"Id\" AS \"OrderId\", \"customer\".\"Name\" AS \"Name\", \"line\".\"Product\" AS \"Product\", \"line\".\"Quantity\" AS \"Quantity\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" INNER JOIN \"order_lines\" AS \"line\" ON \"orders\".\"TenantId\" = \"line\".\"TenantId\" AND \"orders\".\"Id\" = \"line\".\"OrderId\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST, \"line\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R27 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (NOT \"orders\".\"Id\" IN (SELECT \"order_lines\".\"OrderId\" AS \"value\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p2 AND \"order_lines\".\"Quantity\" >= @p3)) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    internal const string R28 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CustomerId\" ASC NULLS FIRST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R29 = "SELECT \"Id\",\"Note\" FROM orders WHERE \"TenantId\"=@p1 ORDER BY \"Id\" LIMIT @p2";
    internal const string R30 = "SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p1 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p2 AND \"order_lines\".\"OrderId\" = @p3 UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p4 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p5 AND \"order_lines\".\"OrderId\" = @p6) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p7 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p8 AND \"order_lines\".\"OrderId\" = @p9) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p10 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p11 AND \"order_lines\".\"OrderId\" = @p12) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p13 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p14 AND \"order_lines\".\"OrderId\" = @p15) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p16 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p17 AND \"order_lines\".\"OrderId\" = @p18) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p19 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p20 AND \"order_lines\".\"OrderId\" = @p21) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p22 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p23 AND \"order_lines\".\"OrderId\" = @p24) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p25 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p26 AND \"order_lines\".\"OrderId\" = @p27) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p28 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p29 AND \"order_lines\".\"OrderId\" = @p30) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p31 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p32 AND \"order_lines\".\"OrderId\" = @p33) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p34 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p35 AND \"order_lines\".\"OrderId\" = @p36) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p37 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p38 AND \"order_lines\".\"OrderId\" = @p39) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p40 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p41 AND \"order_lines\".\"OrderId\" = @p42) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p43 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p44 AND \"order_lines\".\"OrderId\" = @p45) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p46 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p47 AND \"order_lines\".\"OrderId\" = @p48) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p49 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p50 AND \"order_lines\".\"OrderId\" = @p51) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p52 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p53 AND \"order_lines\".\"OrderId\" = @p54) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p55 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p56 AND \"order_lines\".\"OrderId\" = @p57) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p58 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p59 AND \"order_lines\".\"OrderId\" = @p60) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p61 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p62 AND \"order_lines\".\"OrderId\" = @p63) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p64 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p65 AND \"order_lines\".\"OrderId\" = @p66) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p67 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p68 AND \"order_lines\".\"OrderId\" = @p69) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p70 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p71 AND \"order_lines\".\"OrderId\" = @p72) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p73 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p74 AND \"order_lines\".\"OrderId\" = @p75) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p76 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p77 AND \"order_lines\".\"OrderId\" = @p78) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p79 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p80 AND \"order_lines\".\"OrderId\" = @p81) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p82 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p83 AND \"order_lines\".\"OrderId\" = @p84) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p85 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p86 AND \"order_lines\".\"OrderId\" = @p87) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p88 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p89 AND \"order_lines\".\"OrderId\" = @p90) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p91 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p92 AND \"order_lines\".\"OrderId\" = @p93) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @p94 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p95 AND \"order_lines\".\"OrderId\" = @p96)";
    internal const string R31 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string R32 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    internal const string StateSet = "UPDATE \"perf_state\" SET \"Counter\" = @p1, \"Version\" = @p2, \"Note\" = @p3 WHERE \"Id\" = @p4";
    internal const string StateKey = "SELECT \"Id\", \"Counter\", \"Version\", \"Note\" FROM \"perf_state\" WHERE \"Id\" = @p1";
    internal const string StateNote = "UPDATE \"perf_state\" SET \"Note\" = @p1 WHERE \"Id\" = @p2";
    internal const string StateConflict = "UPDATE \"perf_state\" SET \"Counter\" = @p1 WHERE \"Id\" = @p2 AND \"Version\" = @p3";

    internal static string ForRead(string id) => id switch
    {
        "R01" => R01,
        "R02" => R02,
        "R03" => R03,
        "R04" => R04,
        "R05" => R05,
        "R06" => R06,
        "R07" => R07,
        "R08" => R08,
        "R09" => R09,
        "R10" => R10,
        "R11" => R11,
        "R12" => R12,
        "R13" => R13,
        "R14" => R14,
        "R15" => R15,
        "R16" => R16,
        "R17" => R17,
        "R18" => R18,
        "R19" => R19,
        "R20" => R20,
        "R21" => R21,
        "R22" => R22,
        "R23" => R23,
        "R24" => R24,
        "R25" => R25,
        "R26" => R26,
        "R27" => R27,
        "R28" => R28,
        "R29" => R29,
        "R30" => R30,
        "R31" => R31,
        "R32" => R32,
        _ => throw new NotSupportedException("No frozen prepared SQL for " + id),
    };
    internal static string ForState(string shape) => shape switch
    {
        "set" => StateSet,
        "key" => StateKey,
        "note" => StateNote,
        "conflict" => StateConflict,
        _ => throw new NotSupportedException("No frozen prepared state SQL for " + shape),
    };
}

internal sealed partial class DapperWorker
{
    [CacheCommand]
    private ValueTask<Result> ExecuteReadPrepared(ProviderConnection connection, ReadPlan plan) => plan.Case.Id switch
    {
        "R01" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R01, plan.Common!, commandTimeout: 5), HashInteger),
        "R02" => Read(connection.QueryUnbufferedAsync<TextRow>(PreparedSql.R02, plan.Common!, commandTimeout: 5), HashText),
        "R03" => Read(connection.QueryUnbufferedAsync<MoneyRow>(PreparedSql.R03, plan.Common!, commandTimeout: 5), HashMoney),
        "R04" => Read(connection.QueryUnbufferedAsync<JsonRow>(PreparedSql.R04, plan.Common!, commandTimeout: 5), HashJson),
        "R05" => Read(connection.QueryUnbufferedAsync<WideRow>(PreparedSql.R05, plan.Common!, commandTimeout: 5), HashWide),
        "R06" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R06, plan.Common!, commandTimeout: 5), HashInteger),
        "R07" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R07, plan.Common!, commandTimeout: 5), HashInteger),
        "R08" => Read(connection.QueryUnbufferedAsync<JoinRow>(PreparedSql.R08, plan.Common!, commandTimeout: 5), HashJoin),
        "R09" => Read(connection.QueryUnbufferedAsync<JoinRow>(PreparedSql.R09, plan.Common!, commandTimeout: 5), HashJoin),
        "R10" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R10, plan.Common!, commandTimeout: 5), HashInteger),
        "R11" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R11, plan.Literal!, commandTimeout: 5), HashInteger),
        "R12" => Read(connection.QueryUnbufferedAsync<CustomerTotalRow>(PreparedSql.R12, plan.Common!, commandTimeout: 5), HashCustomerTotal),
        "R13" => Read(connection.QueryUnbufferedAsync<ProductTotalRow>(PreparedSql.R13, plan.Common!, commandTimeout: 5), HashProductTotal),
        "R14" => Read(connection.QueryUnbufferedAsync<decimal?>(PreparedSql.R14, plan.Common!, commandTimeout: 5), HashNullableMoney),
        "R15" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R15, plan.Common!, commandTimeout: 5), HashInteger),
        "R16" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R16, plan.Common!, commandTimeout: 5), HashInteger),
        "R17" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R17, plan.Common!, commandTimeout: 5), HashInteger),
        "R18" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R18, plan.Common!, commandTimeout: 5), HashInteger),
        "R19" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R19, plan.Common!, commandTimeout: 5), HashInteger),
        "R20" => Read(connection.QueryUnbufferedAsync<RankRow>(PreparedSql.R20, plan.Common!, commandTimeout: 5), HashRank),
        "R21" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R21, plan.Common!, commandTimeout: 5), HashInteger),
        "R22" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R22, plan.Common!, commandTimeout: 5), HashInteger),
        "R23" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R23, plan.Common!, commandTimeout: 5), HashInteger),
        "R24" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R24, plan.Common!, commandTimeout: 5), HashInteger),
        "R25" => Read(connection.QueryUnbufferedAsync<RunningRow>(PreparedSql.R25, plan.Common!, commandTimeout: 5), HashRunning),
        "R26" => Read(connection.QueryUnbufferedAsync<DetailRow>(PreparedSql.R26, plan.Common!, commandTimeout: 5), HashDetail),
        "R27" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R27, plan.Common!, commandTimeout: 5), HashInteger),
        "R28" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R28, plan.Common!, commandTimeout: 5), HashInteger),
        "R29" => Read(connection.QueryUnbufferedAsync<TextRow>(PreparedSql.R29, plan.Common!, commandTimeout: 5), HashText),
        "R30" => ReadRelated(connection.QueryUnbufferedAsync<RelatedRow>(PreparedSql.R30, plan.Related!, commandTimeout: 5), plan.Case),
        "R31" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R31, plan.Common!, commandTimeout: 5), HashInteger),
        "R32" => Read(connection.QueryUnbufferedAsync<long>(PreparedSql.R32, plan.Common!, commandTimeout: 5), HashInteger),
        _ => throw new NotSupportedException("Prepared read was not frozen into this executable."),
    };

    [CacheCommand]
    private static IAsyncEnumerable<StateRow> QueryStatePrepared(ProviderConnection connection, DbTransaction? transaction, StateStep step) =>
        connection.QueryUnbufferedAsync<StateRow>(PreparedSql.StateKey, step.Key!, transaction: transaction, commandTimeout: 5);

    [CacheCommand]
    private static Task<int> ExecuteStateCommandPrepared(ProviderConnection connection, DbTransaction? transaction, StateStep step) => step.Shape switch
    {
        "set" => connection.ExecuteAsync(PreparedSql.StateSet, step.Set!, transaction: transaction, commandTimeout: 5),
        "note" => connection.ExecuteAsync(PreparedSql.StateNote, step.Note!, transaction: transaction, commandTimeout: 5),
        "conflict" => connection.ExecuteAsync(PreparedSql.StateConflict, step.Conflict!, transaction: transaction, commandTimeout: 5),
        _ => throw new NotSupportedException("Prepared state command was not frozen into this executable."),
    };
}
#endif
