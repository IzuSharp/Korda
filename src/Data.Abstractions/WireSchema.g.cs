// Generated from native/database/interfaces/schema-v1.json; do not edit.
namespace Korda.Data;
internal static class WireSchema
{
    internal const int SchemaVersion = 1;
    internal const int WireVersion = 1;
    internal const int HeaderBytes = 96;
    internal const int DescriptorBytes = 16;
    internal const int CellBytes = 24;
    internal const int ControlHeaderBytes = 16;
    internal const int ControlEntryBytes = 16;
    internal const int RelationControlKind = 23;
    internal static readonly string[] ModelKeys = ["version", "logical_id", "table_id", "schema", "table", "columns", "projection", "relations"];
    internal static readonly string[] ColumnKeys = ["id", "name", "codec", "codec_config", "nullable", "key_ordinal", "generated", "concurrency", "collation", "max_length"];
    internal static readonly string[] ProjectionKeys = ["version", "fields"];
    internal static readonly string[] ProjectionFieldKeys = ["ordinal", "name", "codec", "codec_config", "nullable"];
    internal static readonly string[] RelationKeys = ["version", "parent_model", "parent_table", "child_model", "child_table", "parent_columns", "child_columns", "child_nullable"];
}
