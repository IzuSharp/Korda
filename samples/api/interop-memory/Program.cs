using NativeWeave.Interop;

// The fixture supplies real C++-owned bytes; an application receives the same
// NativeBuffer from its typed engine. It does not construct raw ownership itself.
using NativeBuffer result = Fixture.New([72, 101, 108, 108, 111]);
int count = result.Read(static bytes => bytes.Length);
byte[] retained = result.ToArray();
Console.WriteLine($"Minimal: {count} bytes, copied text {System.Text.Encoding.UTF8.GetString(retained)}");

using var output = new MemoryStream();
await result.CopyToAsync(output, CancellationToken.None);
result.Dispose();
if (!output.ToArray().SequenceEqual(retained) || Fixture.Counter(0) != 0 || Fixture.Counter(4) != 0)
    throw new InvalidOperationException("Advanced copy/retirement failed.");
Console.WriteLine("Advanced: bounded asynchronous copy completed; destination remains open.");
