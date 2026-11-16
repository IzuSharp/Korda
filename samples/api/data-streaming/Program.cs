using NativeWeave.Data;
using NativeWeave.Database.Internal;

// Maintainer component sample: the factory uses the test-only native ABI shim.
// The future public DataSession/QuerySelection owners supply the same adapter seam.
var provider = args.Length > 0 && args[0] == "postgresql" ? 2u : 1u;
var projection = Projection<StreamingRow>.Create(
    [ResultField.Create("id", Codecs.Int64), ResultField.Create("label", Codecs.NullableReference(Codecs.String))],
    row => new(row.Read(0, Codecs.Int64), row.Read(1, Codecs.NullableReference(Codecs.String))));
var limits = new StreamLimits(3, 256, 100, 64 * 1024);
NativeFixtureTransport? transport = null;
TypedStream<StreamingRow> Execute() => new(async (rows, token) =>
{
    transport = await NativeFixtureTransport.OpenAsync(provider, 2, projection.CanonicalBytes, rows, limits.MaxBatchBytes, token);
    return transport;
}, projection, provider, 7, [3, 5], limits);

var count = 0;
await foreach (var row in Execute())
{
    Console.WriteLine($"{row.Id}: {row.Label}");
    if (++count == 3) break;
}
if (transport!.Stats()[8] != 1 || transport.Stats()[12] != 1) throw new InvalidOperationException("Early break did not retire the native session.");

IReadOnlyList<StreamingRow> owned = await Execute().ToListAsync();
if (owned.Count != 17 || owned[16].Label != "row-17") throw new InvalidOperationException("Bounded result differs.");
Console.WriteLine($"PASS component sample provider={provider}; early-break={count}; owned-list={owned.Count}; cleanup={transport.Stats()[8]}");

internal sealed record StreamingRow(long Id, string? Label);
