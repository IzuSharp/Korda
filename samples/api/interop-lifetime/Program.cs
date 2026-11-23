using Korda.Interop.Lifetime;

// Minimal maintainer consumer: applications receive their domain's typed owner.
using (NativeResourceOwner owner = Fixture.New(7))
    Console.WriteLine($"Minimal leased native result: {Fixture.Invoke(owner)}");

// Advanced: physical retirement is distinct from requesting synchronous close.
var retained = Fixture.New(19);
bool admitted = false;
try
{
    nint pointer = retained.Acquire(ref admitted);
    retained.Dispose();
    if (retained.Retirement.IsCompleted) throw new InvalidOperationException("Premature retirement.");
    if (Fixture.Read(pointer, out ulong value, out _) != 0 || value != 19)
        throw new InvalidOperationException("Existing lease must survive logical closure.");
}
finally { retained.Release(ref admitted); }
await retained.DisposeAsync();
if (Fixture.Counter(0) != 0 || Fixture.Counter(2) != 0) throw new InvalidOperationException("Leaked fixture owner.");
Console.WriteLine("Advanced close and physical retirement completed.");
