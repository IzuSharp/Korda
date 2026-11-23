using System.Text.Json.Serialization;
using Korda.Data;
using Korda.Database;

namespace Korda.Samples.Materialization;

internal sealed record Settings(long Level, string Tag);
internal sealed record Envelope<T>(T Value);
internal sealed record OwnedRow(long Id, string Name, byte[] Bytes, long? Optional, Envelope<Settings> Settings);

// This is ordinary application source. No runtime type discovery, reflection setter,
// expression compilation, generator dependency or native rebuild is involved.
internal static class MaterializationExamples
{
    internal static ValueCodec<Envelope<Settings>> SettingsCodec { get; } = Codecs.Json(SampleJson.Default.EnvelopeSettings);
    internal static Projection<OwnedRow> Projection { get; } = Projection<OwnedRow>.Create(
        [ResultField.Create("id", Codecs.Int64), ResultField.Create("name", Codecs.String),
         ResultField.Create("bytes", Codecs.Bytes), ResultField.Create("optional", Codecs.Nullable(Codecs.Int64)),
         ResultField.Create("settings", SettingsCodec)],
        row => new OwnedRow(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String),
            row.Read(2, Codecs.Bytes), row.Read(3, Codecs.Nullable(Codecs.Int64)), row.Read(4, SettingsCodec)));

    internal static async Task<IReadOnlyList<OwnedRow>> ReadAsync(DataSession session, SqlCommand command, CancellationToken cancellationToken = default)
        => await session.QueryAsync(command, Projection, cancellationToken);

    internal static async Task<long> SumAsync(DataSession session, SqlCommand command, CancellationToken cancellationToken = default)
    {
        long total = 0;
        await foreach (var row in session.StreamAsync(command, Projection, cancellationToken))
            total = checked(total + row.Id);
        return total;
    }
}

[JsonSerializable(typeof(Envelope<Settings>))]
internal partial class SampleJson : JsonSerializerContext { }
