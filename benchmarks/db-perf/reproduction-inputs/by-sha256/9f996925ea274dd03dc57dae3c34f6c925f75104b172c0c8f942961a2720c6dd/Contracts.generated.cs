// Generated from workload/queries-v2.json; query constants are required by Dapper.AOT command reuse.
#nullable enable
using System.Data.Common;
using System.Text;
using Dapper;

internal sealed partial class Workload
{
#if POSTGRES
    private const string Sql0 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql1 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Note\" AS \"Note\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql2 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Total\" AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql3 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Settings\" AS \"Settings\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql4 = "SELECT \"orders\".\"TenantId\" AS \"TenantId\", \"orders\".\"Id\" AS \"Id\", \"orders\".\"CreatedAt\" AS \"CreatedAt\", \"orders\".\"CustomerId\" AS \"CustomerId\", \"orders\".\"Total\" AS \"Total\", \"orders\".\"Note\" AS \"Note\", \"orders\".\"Version\" AS \"Version\", \"orders\".\"Settings\" AS \"Settings\", \"orders\".\"InternalMemo\" AS \"InternalMemo\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql5 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"CustomerId\" = @p2 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql6 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" >= @p2 AND \"orders\".\"CreatedAt\" < @p3) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    private const string Sql7 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" LEFT JOIN (SELECT \"Email\", \"Name\", \"Id\", 1 AS \"_nw_match\" FROM \"customers\") AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql8 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql9 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" IS NULL ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql10 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" LIKE @pattern ESCAPE E'\\\\' ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql11 = "SELECT \"orders\".\"CustomerId\" AS \"CustomerId\", COUNT(*) AS \"Count\", SUM(\"orders\".\"Total\") AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 GROUP BY \"orders\".\"CustomerId\" HAVING COUNT(*) > @p2 ORDER BY \"orders\".\"CustomerId\" ASC NULLS LAST";
    private const string Sql12 = "SELECT \"order_lines\".\"Product\" AS \"Product\", CAST(SUM(\"order_lines\".\"Quantity\") AS bigint) AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 GROUP BY \"order_lines\".\"Product\" HAVING CAST(SUM(\"order_lines\".\"Quantity\") AS bigint) > @p2 ORDER BY \"order_lines\".\"Product\" ASC NULLS LAST";
    private const string Sql13 = "SELECT SUM(\"orders\".\"Total\") AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1";
    private const string Sql14 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" > @p2 OR (\"orders\".\"CreatedAt\" = @p3 AND (\"orders\".\"TenantId\" > @p4 OR (\"orders\".\"TenantId\" = @p5 AND \"orders\".\"Id\" > @p6)))) ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p7";
    private const string Sql15 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2 OFFSET @p3";
    private const string Sql16 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE EXISTS(SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" >= @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql17 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE \"customers\".\"Id\" IN (SELECT \"orders\".\"CustomerId\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql18 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql19 = "SELECT \"orders\".\"Id\" AS \"Id\", (ROW_NUMBER() OVER ( PARTITION BY \"orders\".\"CustomerId\" ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST )) AS \"Rank\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql20 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION ALL (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql21 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql22 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 INTERSECT (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql23 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 EXCEPT (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5))) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql24 = "SELECT \"order_lines\".\"Id\" AS \"Id\", CAST(((SUM(\"order_lines\".\"Quantity\") OVER (  ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW ))) AS bigint) AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql25 = "SELECT \"orders\".\"Id\" AS \"OrderId\", \"customer\".\"Name\" AS \"Name\", \"line\".\"Product\" AS \"Product\", \"line\".\"Quantity\" AS \"Quantity\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" INNER JOIN \"order_lines\" AS \"line\" ON \"orders\".\"TenantId\" = \"line\".\"TenantId\" AND \"orders\".\"Id\" = \"line\".\"OrderId\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST, \"line\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql26 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (NOT \"orders\".\"Id\" IN (SELECT \"order_lines\".\"OrderId\" AS \"value\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p2 AND \"order_lines\".\"Quantity\" >= @p3)) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    private const string Sql27 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CustomerId\" ASC NULLS FIRST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql28 = "SELECT \"Id\",\"Note\" FROM orders WHERE \"TenantId\"=@p1 ORDER BY \"Id\" LIMIT @p2";
    private const string Sql29 = "SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_0 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_0 AND \"order_lines\".\"OrderId\" = @parent_id_0 UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_1 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_1 AND \"order_lines\".\"OrderId\" = @parent_id_1) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_2 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_2 AND \"order_lines\".\"OrderId\" = @parent_id_2) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_3 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_3 AND \"order_lines\".\"OrderId\" = @parent_id_3) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_4 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_4 AND \"order_lines\".\"OrderId\" = @parent_id_4) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_5 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_5 AND \"order_lines\".\"OrderId\" = @parent_id_5) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_6 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_6 AND \"order_lines\".\"OrderId\" = @parent_id_6) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_7 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_7 AND \"order_lines\".\"OrderId\" = @parent_id_7) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_8 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_8 AND \"order_lines\".\"OrderId\" = @parent_id_8) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_9 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_9 AND \"order_lines\".\"OrderId\" = @parent_id_9) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_10 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_10 AND \"order_lines\".\"OrderId\" = @parent_id_10) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_11 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_11 AND \"order_lines\".\"OrderId\" = @parent_id_11) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_12 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_12 AND \"order_lines\".\"OrderId\" = @parent_id_12) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_13 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_13 AND \"order_lines\".\"OrderId\" = @parent_id_13) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_14 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_14 AND \"order_lines\".\"OrderId\" = @parent_id_14) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_15 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_15 AND \"order_lines\".\"OrderId\" = @parent_id_15) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_16 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_16 AND \"order_lines\".\"OrderId\" = @parent_id_16) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_17 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_17 AND \"order_lines\".\"OrderId\" = @parent_id_17) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_18 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_18 AND \"order_lines\".\"OrderId\" = @parent_id_18) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_19 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_19 AND \"order_lines\".\"OrderId\" = @parent_id_19) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_20 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_20 AND \"order_lines\".\"OrderId\" = @parent_id_20) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_21 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_21 AND \"order_lines\".\"OrderId\" = @parent_id_21) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_22 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_22 AND \"order_lines\".\"OrderId\" = @parent_id_22) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_23 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_23 AND \"order_lines\".\"OrderId\" = @parent_id_23) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_24 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_24 AND \"order_lines\".\"OrderId\" = @parent_id_24) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_25 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_25 AND \"order_lines\".\"OrderId\" = @parent_id_25) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_26 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_26 AND \"order_lines\".\"OrderId\" = @parent_id_26) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_27 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_27 AND \"order_lines\".\"OrderId\" = @parent_id_27) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_28 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_28 AND \"order_lines\".\"OrderId\" = @parent_id_28) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_29 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_29 AND \"order_lines\".\"OrderId\" = @parent_id_29) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_30 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_30 AND \"order_lines\".\"OrderId\" = @parent_id_30) UNION ALL (SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_31 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_31 AND \"order_lines\".\"OrderId\" = @parent_id_31)";
#else
    private const string Sql0 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql1 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Note\" AS \"Note\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql2 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Total\" AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql3 = "SELECT \"orders\".\"Id\" AS \"Id\", \"orders\".\"Settings\" AS \"Settings\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql4 = "SELECT \"orders\".\"TenantId\" AS \"TenantId\", \"orders\".\"Id\" AS \"Id\", \"orders\".\"CreatedAt\" AS \"CreatedAt\", \"orders\".\"CustomerId\" AS \"CustomerId\", \"orders\".\"Total\" AS \"Total\", \"orders\".\"Note\" AS \"Note\", \"orders\".\"Version\" AS \"Version\", \"orders\".\"Settings\" AS \"Settings\", \"orders\".\"InternalMemo\" AS \"InternalMemo\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql5 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"CustomerId\" = @p2 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql6 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" >= @p2 AND \"orders\".\"CreatedAt\" < @p3) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    private const string Sql7 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" LEFT JOIN (SELECT \"Email\", \"Name\", \"Id\", 1 AS \"_nw_match\" FROM \"customers\") AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql8 = "SELECT \"orders\".\"Id\" AS \"Id\", \"customer\".\"Name\" AS \"Name\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql9 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" IS NULL ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql10 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Note\" LIKE @pattern ESCAPE '\\' ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql11 = "SELECT \"orders\".\"CustomerId\" AS \"CustomerId\", COUNT(*) AS \"Count\", SUM(\"orders\".\"Total\") AS \"Total\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 GROUP BY \"orders\".\"CustomerId\" HAVING COUNT(*) > @p2 ORDER BY \"orders\".\"CustomerId\" ASC NULLS LAST";
    private const string Sql12 = "SELECT \"order_lines\".\"Product\" AS \"Product\", SUM(\"order_lines\".\"Quantity\") AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 GROUP BY \"order_lines\".\"Product\" HAVING SUM(\"order_lines\".\"Quantity\") > @p2 ORDER BY \"order_lines\".\"Product\" ASC NULLS LAST";
    private const string Sql13 = "SELECT SUM(\"orders\".\"Total\") AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1";
    private const string Sql14 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (\"orders\".\"CreatedAt\" > @p2 OR (\"orders\".\"CreatedAt\" = @p3 AND (\"orders\".\"TenantId\" > @p4 OR (\"orders\".\"TenantId\" = @p5 AND \"orders\".\"Id\" > @p6)))) ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p7";
    private const string Sql15 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"TenantId\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2 OFFSET @p3";
    private const string Sql16 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE EXISTS(SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" >= @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql17 = "SELECT \"customers\".\"Id\" AS \"value\" FROM \"customers\" WHERE \"customers\".\"Id\" IN (SELECT \"orders\".\"CustomerId\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) ORDER BY \"customers\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql18 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" > @p2) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST LIMIT @p3";
    private const string Sql19 = "SELECT \"orders\".\"Id\" AS \"Id\", (ROW_NUMBER() OVER ( PARTITION BY \"orders\".\"CustomerId\" ORDER BY \"orders\".\"CreatedAt\" ASC NULLS LAST, \"orders\".\"Id\" ASC NULLS LAST )) AS \"Rank\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql20 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION ALL SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql21 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 UNION SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql22 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 INTERSECT SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql23 = "WITH \"selected_order_ids\" (\"Id\") AS (SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND \"orders\".\"Id\" <= @p2 EXCEPT SELECT \"orders\".\"Id\" AS \"Id\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p3 AND (\"orders\".\"Id\" >= @p4 AND \"orders\".\"Id\" <= @p5)) SELECT \"selected_order_ids\".\"Id\" AS \"value\" FROM \"selected_order_ids\" ORDER BY \"selected_order_ids\".\"Id\" ASC NULLS LAST";
    private const string Sql24 = "SELECT \"order_lines\".\"Id\" AS \"Id\", (SUM(\"order_lines\".\"Quantity\") OVER (  ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW )) AS \"Quantity\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p1 ORDER BY \"order_lines\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql25 = "SELECT \"orders\".\"Id\" AS \"OrderId\", \"customer\".\"Name\" AS \"Name\", \"line\".\"Product\" AS \"Product\", \"line\".\"Quantity\" AS \"Quantity\" FROM \"orders\" INNER JOIN \"customers\" AS \"customer\" ON \"orders\".\"CustomerId\" = \"customer\".\"Id\" INNER JOIN \"order_lines\" AS \"line\" ON \"orders\".\"TenantId\" = \"line\".\"TenantId\" AND \"orders\".\"Id\" = \"line\".\"OrderId\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"Id\" ASC NULLS LAST, \"line\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql26 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 AND (NOT \"orders\".\"Id\" IN (SELECT \"order_lines\".\"OrderId\" AS \"value\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @p2 AND \"order_lines\".\"Quantity\" >= @p3)) ORDER BY \"orders\".\"Id\" ASC NULLS LAST LIMIT @p4";
    private const string Sql27 = "SELECT \"orders\".\"Id\" AS \"value\" FROM \"orders\" WHERE \"orders\".\"TenantId\" = @p1 ORDER BY \"orders\".\"CustomerId\" ASC NULLS FIRST, \"orders\".\"Id\" ASC NULLS LAST LIMIT @p2";
    private const string Sql28 = "SELECT \"Id\",\"Note\" FROM orders WHERE \"TenantId\"=@p1 ORDER BY \"Id\" LIMIT @p2";
    private const string Sql29 = "SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_0 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_0 AND \"order_lines\".\"OrderId\" = @parent_id_0 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_1 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_1 AND \"order_lines\".\"OrderId\" = @parent_id_1 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_2 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_2 AND \"order_lines\".\"OrderId\" = @parent_id_2 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_3 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_3 AND \"order_lines\".\"OrderId\" = @parent_id_3 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_4 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_4 AND \"order_lines\".\"OrderId\" = @parent_id_4 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_5 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_5 AND \"order_lines\".\"OrderId\" = @parent_id_5 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_6 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_6 AND \"order_lines\".\"OrderId\" = @parent_id_6 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_7 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_7 AND \"order_lines\".\"OrderId\" = @parent_id_7 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_8 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_8 AND \"order_lines\".\"OrderId\" = @parent_id_8 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_9 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_9 AND \"order_lines\".\"OrderId\" = @parent_id_9 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_10 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_10 AND \"order_lines\".\"OrderId\" = @parent_id_10 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_11 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_11 AND \"order_lines\".\"OrderId\" = @parent_id_11 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_12 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_12 AND \"order_lines\".\"OrderId\" = @parent_id_12 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_13 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_13 AND \"order_lines\".\"OrderId\" = @parent_id_13 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_14 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_14 AND \"order_lines\".\"OrderId\" = @parent_id_14 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_15 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_15 AND \"order_lines\".\"OrderId\" = @parent_id_15 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_16 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_16 AND \"order_lines\".\"OrderId\" = @parent_id_16 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_17 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_17 AND \"order_lines\".\"OrderId\" = @parent_id_17 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_18 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_18 AND \"order_lines\".\"OrderId\" = @parent_id_18 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_19 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_19 AND \"order_lines\".\"OrderId\" = @parent_id_19 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_20 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_20 AND \"order_lines\".\"OrderId\" = @parent_id_20 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_21 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_21 AND \"order_lines\".\"OrderId\" = @parent_id_21 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_22 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_22 AND \"order_lines\".\"OrderId\" = @parent_id_22 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_23 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_23 AND \"order_lines\".\"OrderId\" = @parent_id_23 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_24 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_24 AND \"order_lines\".\"OrderId\" = @parent_id_24 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_25 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_25 AND \"order_lines\".\"OrderId\" = @parent_id_25 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_26 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_26 AND \"order_lines\".\"OrderId\" = @parent_id_26 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_27 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_27 AND \"order_lines\".\"OrderId\" = @parent_id_27 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_28 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_28 AND \"order_lines\".\"OrderId\" = @parent_id_28 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_29 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_29 AND \"order_lines\".\"OrderId\" = @parent_id_29 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_30 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_30 AND \"order_lines\".\"OrderId\" = @parent_id_30 UNION ALL SELECT \"order_lines\".\"Id\" AS \"Id\", \"order_lines\".\"TenantId\" AS \"TenantId\", \"order_lines\".\"OrderId\" AS \"OrderId\", \"order_lines\".\"Product\" AS \"Product\", \"order_lines\".\"Quantity\" AS \"Quantity\", @parent_index_31 AS \"__korda_parent_index\" FROM \"order_lines\" WHERE \"order_lines\".\"TenantId\" = @tenant_31 AND \"order_lines\".\"OrderId\" = @parent_id_31";
#endif
    private static readonly string[] ContractSql = [Sql0,Sql1,Sql2,Sql3,Sql4,Sql5,Sql6,Sql7,Sql8,Sql9,Sql10,Sql11,Sql12,Sql13,Sql14,Sql15,Sql16,Sql17,Sql18,Sql19,Sql20,Sql21,Sql22,Sql23,Sql24,Sql25,Sql26,Sql27,Sql28,Sql29];
    private static readonly string[][] ContractTokens = ContractSql.Select(sql => sql.Split(' ')).ToArray();
    private static string BuildContractSql(int index)
    {
        var builder=new StringBuilder(); var tokens=ContractTokens[index];
        for(var i=0;i<tokens.Length;i++){if(i!=0)builder.Append(' ');builder.Append(tokens[i]);}
        return builder.ToString();
    }
    private Parameters CreateArguments(int index) => index switch
    {
        0=>new(p1:1L,p2:(long)_options.Rows),
        1=>new(p1:1L,p2:(long)_options.Rows),
        2=>new(p1:1L,p2:(long)_options.Rows),
        3=>new(p1:1L,p2:(long)_options.Rows),
        4=>new(p1:1L,p2:(long)_options.Rows),
        5=>new(p1:1L,p2:2L,p3:(long)_options.Rows),
        6=>new(p1:1L,p2:1700000100L,p3:1700000200L,p4:(long)_options.Rows),
        7=>new(p1:1L,p2:(long)_options.Rows),
        8=>new(p1:1L,p2:(long)_options.Rows),
        9=>new(p1:1L,p2:(long)_options.Rows),
        10=>new(p1:1L,pattern:"%note-1%",p3:(long)_options.Rows),
        11=>new(p1:1L,p2:3L),
        12=>new(p1:1L,p2:10L),
        13=>new(p1:-1L),
        14=>new(p1:1L,p2:1700000000L+(_options.OrdersPerTenant/2)/4,p3:1700000000L+(_options.OrdersPerTenant/2)/4,p4:1L,p5:1L,p6:(long)(_options.OrdersPerTenant/2),p7:(long)_options.Rows),
        15=>new(p1:1L,p2:(long)_options.Rows,p3:(long)(_options.OrdersPerTenant*3/4)),
        16=>new(p1:1L,p2:32L,p3:1L),
        17=>new(p1:1L,p2:128L,p3:(long)_options.Rows),
        18=>new(p1:1L,p2:16L,p3:(long)_options.Rows),
        19=>new(p1:1L,p2:(long)_options.Rows),
        20=>new(p1:1L,p2:16L,p3:1L,p4:9L,p5:24L),
        21=>new(p1:1L,p2:16L,p3:1L,p4:9L,p5:24L),
        22=>new(p1:1L,p2:16L,p3:1L,p4:9L,p5:24L),
        23=>new(p1:1L,p2:16L,p3:1L,p4:9L,p5:24L),
        24=>new(p1:1L,p2:(long)_options.Rows),
        25=>new(p1:1L,p2:(long)_options.Rows),
        26=>new(p1:1L,p2:1L,p3:5L,p4:(long)_options.Rows),
        27=>new(p1:1L,p2:(long)_options.Rows),
        28=>new(p1:1L,p2:(long)_options.Rows),
        _=>new(),
    };
    [CacheCommand]
    private ValueTask<OperationResult> Reused(DbConnection connection,int index) => index switch
    {
        0=>Read(connection.QueryUnbufferedAsync<long>(Sql0,_arguments[index]),HashInteger),
        1=>Read(connection.QueryUnbufferedAsync<TextRow>(Sql1,_arguments[index]),HashText),
        2=>Read(connection.QueryUnbufferedAsync<MoneyRow>(Sql2,_arguments[index]),HashMoney),
        3=>Read(connection.QueryUnbufferedAsync<JsonRow>(Sql3,_arguments[index]),HashJson),
        4=>Read(connection.QueryUnbufferedAsync<Order>(Sql4,_arguments[index]),HashOrder),
        5=>Read(connection.QueryUnbufferedAsync<long>(Sql5,_arguments[index]),HashInteger),
        6=>Read(connection.QueryUnbufferedAsync<long>(Sql6,_arguments[index]),HashInteger),
        7=>Read(connection.QueryUnbufferedAsync<JoinRow>(Sql7,_arguments[index]),HashJoin),
        8=>Read(connection.QueryUnbufferedAsync<JoinRow>(Sql8,_arguments[index]),HashJoin),
        9=>Read(connection.QueryUnbufferedAsync<long>(Sql9,_arguments[index]),HashInteger),
        10=>Read(connection.QueryUnbufferedAsync<long>(Sql10,_arguments[index]),HashInteger),
        11=>Read(connection.QueryUnbufferedAsync<CustomerTotal>(Sql11,_arguments[index]),HashCustomerTotal),
        12=>Read(connection.QueryUnbufferedAsync<ProductTotal>(Sql12,_arguments[index]),HashProductTotal),
        13=>Read(connection.QueryUnbufferedAsync<decimal?>(Sql13,_arguments[index]),HashNullableMoney),
        14=>Read(connection.QueryUnbufferedAsync<long>(Sql14,_arguments[index]),HashInteger),
        15=>Read(connection.QueryUnbufferedAsync<long>(Sql15,_arguments[index]),HashInteger),
        16=>Read(connection.QueryUnbufferedAsync<long>(Sql16,_arguments[index]),HashInteger),
        17=>Read(connection.QueryUnbufferedAsync<long>(Sql17,_arguments[index]),HashInteger),
        18=>Read(connection.QueryUnbufferedAsync<long>(Sql18,_arguments[index]),HashInteger),
        19=>Read(connection.QueryUnbufferedAsync<RankRow>(Sql19,_arguments[index]),HashRank),
        20=>Read(connection.QueryUnbufferedAsync<long>(Sql20,_arguments[index]),HashInteger),
        21=>Read(connection.QueryUnbufferedAsync<long>(Sql21,_arguments[index]),HashInteger),
        22=>Read(connection.QueryUnbufferedAsync<long>(Sql22,_arguments[index]),HashInteger),
        23=>Read(connection.QueryUnbufferedAsync<long>(Sql23,_arguments[index]),HashInteger),
        24=>Read(connection.QueryUnbufferedAsync<RunningRow>(Sql24,_arguments[index]),HashRunning),
        25=>Read(connection.QueryUnbufferedAsync<DetailRow>(Sql25,_arguments[index]),HashDetail),
        26=>Read(connection.QueryUnbufferedAsync<long>(Sql26,_arguments[index]),HashInteger),
        27=>Read(connection.QueryUnbufferedAsync<long>(Sql27,_arguments[index]),HashInteger),
        28=>Read(connection.QueryUnbufferedAsync<TextRow>(Sql28,_arguments[index]),HashText),
        29=>Related(connection,true),
        _=>throw new ArgumentOutOfRangeException(nameof(index)),
    };
    [CacheCommand(false)]
    private ValueTask<OperationResult> Dynamic(DbConnection connection,int index)
    {
        if(index==29)return Related(connection,false);
        var sql=BuildContractSql(index);var parameters=CreateArguments(index);
        return index switch
        {
            0=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            1=>Read(connection.QueryUnbufferedAsync<TextRow>(sql,parameters),HashText),
            2=>Read(connection.QueryUnbufferedAsync<MoneyRow>(sql,parameters),HashMoney),
            3=>Read(connection.QueryUnbufferedAsync<JsonRow>(sql,parameters),HashJson),
            4=>Read(connection.QueryUnbufferedAsync<Order>(sql,parameters),HashOrder),
            5=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            6=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            7=>Read(connection.QueryUnbufferedAsync<JoinRow>(sql,parameters),HashJoin),
            8=>Read(connection.QueryUnbufferedAsync<JoinRow>(sql,parameters),HashJoin),
            9=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            10=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            11=>Read(connection.QueryUnbufferedAsync<CustomerTotal>(sql,parameters),HashCustomerTotal),
            12=>Read(connection.QueryUnbufferedAsync<ProductTotal>(sql,parameters),HashProductTotal),
            13=>Read(connection.QueryUnbufferedAsync<decimal?>(sql,parameters),HashNullableMoney),
            14=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            15=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            16=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            17=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            18=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            19=>Read(connection.QueryUnbufferedAsync<RankRow>(sql,parameters),HashRank),
            20=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            21=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            22=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            23=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            24=>Read(connection.QueryUnbufferedAsync<RunningRow>(sql,parameters),HashRunning),
            25=>Read(connection.QueryUnbufferedAsync<DetailRow>(sql,parameters),HashDetail),
            26=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            27=>Read(connection.QueryUnbufferedAsync<long>(sql,parameters),HashInteger),
            28=>Read(connection.QueryUnbufferedAsync<TextRow>(sql,parameters),HashText),
            _=>throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }
    internal sealed record Parameters(long p1=0,long p2=0,long p3=0,long p4=0,long p5=0,long p6=0,long p7=0,string? pattern=null);
    private readonly RelatedParameters _relatedParameters=new();
    internal sealed class RelatedParameters
    {
        public int parent_index_0 {get;} = 0;
        public long tenant_0 {get;} = 1;
        public long parent_id_0 {get;} = 1;
        public int parent_index_1 {get;} = 1;
        public long tenant_1 {get;} = 1;
        public long parent_id_1 {get;} = 2;
        public int parent_index_2 {get;} = 2;
        public long tenant_2 {get;} = 1;
        public long parent_id_2 {get;} = 3;
        public int parent_index_3 {get;} = 3;
        public long tenant_3 {get;} = 1;
        public long parent_id_3 {get;} = 4;
        public int parent_index_4 {get;} = 4;
        public long tenant_4 {get;} = 1;
        public long parent_id_4 {get;} = 5;
        public int parent_index_5 {get;} = 5;
        public long tenant_5 {get;} = 1;
        public long parent_id_5 {get;} = 6;
        public int parent_index_6 {get;} = 6;
        public long tenant_6 {get;} = 1;
        public long parent_id_6 {get;} = 7;
        public int parent_index_7 {get;} = 7;
        public long tenant_7 {get;} = 1;
        public long parent_id_7 {get;} = 8;
        public int parent_index_8 {get;} = 8;
        public long tenant_8 {get;} = 1;
        public long parent_id_8 {get;} = 9;
        public int parent_index_9 {get;} = 9;
        public long tenant_9 {get;} = 1;
        public long parent_id_9 {get;} = 10;
        public int parent_index_10 {get;} = 10;
        public long tenant_10 {get;} = 1;
        public long parent_id_10 {get;} = 11;
        public int parent_index_11 {get;} = 11;
        public long tenant_11 {get;} = 1;
        public long parent_id_11 {get;} = 12;
        public int parent_index_12 {get;} = 12;
        public long tenant_12 {get;} = 1;
        public long parent_id_12 {get;} = 13;
        public int parent_index_13 {get;} = 13;
        public long tenant_13 {get;} = 1;
        public long parent_id_13 {get;} = 14;
        public int parent_index_14 {get;} = 14;
        public long tenant_14 {get;} = 1;
        public long parent_id_14 {get;} = 15;
        public int parent_index_15 {get;} = 15;
        public long tenant_15 {get;} = 1;
        public long parent_id_15 {get;} = 16;
        public int parent_index_16 {get;} = 16;
        public long tenant_16 {get;} = 1;
        public long parent_id_16 {get;} = 17;
        public int parent_index_17 {get;} = 17;
        public long tenant_17 {get;} = 1;
        public long parent_id_17 {get;} = 18;
        public int parent_index_18 {get;} = 18;
        public long tenant_18 {get;} = 1;
        public long parent_id_18 {get;} = 19;
        public int parent_index_19 {get;} = 19;
        public long tenant_19 {get;} = 1;
        public long parent_id_19 {get;} = 20;
        public int parent_index_20 {get;} = 20;
        public long tenant_20 {get;} = 1;
        public long parent_id_20 {get;} = 21;
        public int parent_index_21 {get;} = 21;
        public long tenant_21 {get;} = 1;
        public long parent_id_21 {get;} = 22;
        public int parent_index_22 {get;} = 22;
        public long tenant_22 {get;} = 1;
        public long parent_id_22 {get;} = 23;
        public int parent_index_23 {get;} = 23;
        public long tenant_23 {get;} = 1;
        public long parent_id_23 {get;} = 24;
        public int parent_index_24 {get;} = 24;
        public long tenant_24 {get;} = 1;
        public long parent_id_24 {get;} = 25;
        public int parent_index_25 {get;} = 25;
        public long tenant_25 {get;} = 1;
        public long parent_id_25 {get;} = 26;
        public int parent_index_26 {get;} = 26;
        public long tenant_26 {get;} = 1;
        public long parent_id_26 {get;} = 27;
        public int parent_index_27 {get;} = 27;
        public long tenant_27 {get;} = 1;
        public long parent_id_27 {get;} = 28;
        public int parent_index_28 {get;} = 28;
        public long tenant_28 {get;} = 1;
        public long parent_id_28 {get;} = 29;
        public int parent_index_29 {get;} = 29;
        public long tenant_29 {get;} = 1;
        public long parent_id_29 {get;} = 30;
        public int parent_index_30 {get;} = 30;
        public long tenant_30 {get;} = 1;
        public long parent_id_30 {get;} = 31;
        public int parent_index_31 {get;} = 31;
        public long tenant_31 {get;} = 1;
        public long parent_id_31 {get;} = 32;
    }
    [CacheCommand]
    private Task<IEnumerable<IndexedLine>> RelatedReused(DbConnection connection) => connection.QueryAsync<IndexedLine>(Sql29,_relatedParameters);
    [CacheCommand(false)]
    private Task<IEnumerable<IndexedLine>> RelatedDynamic(DbConnection connection) => connection.QueryAsync<IndexedLine>(BuildContractSql(29),_relatedParameters);
    private async ValueTask<OperationResult> Related(DbConnection connection,bool prepared)
    {
        if(_options.Rows!=32||_options.BatchRows<32)throw new ArgumentException("The frozen related contract contains exactly32 parent keys; regenerate for other parent batches.");
        var pending=prepared?RelatedReused(connection):RelatedDynamic(connection);var suspensions=pending.IsCompleted?0:1;
        var buffered=await pending.ConfigureAwait(false);var groups=new List<OrderLine>?[32];
        foreach(var row in buffered)
        {
            if((uint)row.__korda_parent_index>=32)throw new InvalidOperationException("Invalid parent ordinal");
            (groups[row.__korda_parent_index]??=[]).Add(new(row.Id,row.TenantId,row.OrderId,row.Product,row.Quantity));
        }
        var digest=Digest.Create();long count=0;
        for(var index=0;index<groups.Length;index++)
        {
            digest.Integer(index+1);digest.Integer(groups[index]?.Count??0);
            if(groups[index] is {} group)foreach(var row in group.OrderBy(static row=>row.Id)){HashLine(ref digest,row);count++;}
        }
        return new(count,digest.Value,suspensions);
    }
}
