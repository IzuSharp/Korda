using Korda.Interop;

namespace Korda.Database.Sqlite;

/// <summary>Creates SQLite datasources backed by the maintainer-built C++ Database Host.</summary>
/// <remarks>Descriptor access is pure metadata. Inspect verifies the packaged asset without creating a runtime or opening a connection; Create owns the configured runtime and datasource.</remarks>
public static partial class SqliteDataSource
{
    /// <summary>Gets the immutable descriptor of the complete SQLite Database Host asset.</summary>
    /// <remarks>The canonical database bootstrap negotiates only capabilities implemented by this asset. Missing or mismatched assets fail inspection before runtime admission.</remarks>
    public static EngineDescriptor Engine => HostEngine;
}
