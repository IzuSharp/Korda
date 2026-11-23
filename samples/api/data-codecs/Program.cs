using System.Text.Json.Serialization;
using Korda.Data;

// Application-facing declarations are canonical B01 types. B07 supplies their
// checked native conversion; the complete source/session host belongs to B04.
var simple = new SqlParameters()
    .Add("id", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Codecs.Guid)
    .Add("enabled", true, Codecs.Boolean);
_ = SqlCommand.Create("SELECT @id, @enabled", simple);

// Explicit codecs keep precision, typed NULL and static JSON metadata visible.
// No wall-clock or rounding assumption is made by this sample.
var advanced = new SqlParameters()
    .Add("amount", 123.45m, Codecs.Decimal(18, 2))
    .Add("instant", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Codecs.UtcInstant)
    .Add("note", (string?)null, Codecs.NullableReference(Codecs.String))
    .Add("payload", new Payload("example"), Codecs.Json(SampleJsonContext.Default.Payload));
_ = SqlCommand.Create("SELECT @amount, @instant, @note, @payload", advanced);

// SQLite DecimalText is exact TEXT storage, without numeric SQL operations.
_ = SqlCommand.Create("SELECT @amount", new SqlParameters().Add("amount", 123.45m, Codecs.DecimalText));
try
{
    _ = new SqlParameters().Add("amount", 1.001m, Codecs.Decimal(18, 2));
    throw new InvalidOperationException("Lossy conversion unexpectedly passed.");
}
catch (ArgumentException)
{
    Console.WriteLine("Typed snapshots prepared; precision loss rejected before provider admission.");
}
internal sealed record Payload(string Name);
[JsonSerializable(typeof(Payload))]
internal partial class SampleJsonContext : JsonSerializerContext;
