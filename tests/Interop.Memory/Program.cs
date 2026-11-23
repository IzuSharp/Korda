using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Extensibility;
using Korda.Interop.Memory;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool value, string name) { Interlocked.Increment(ref _assertions); if (!value) throw new InvalidOperationException(name); }
    private static void Throws<T>(Action body) where T : Exception
    { try { body(); } catch (T) { Check(true, typeof(T).Name); return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Canceled(Task task, CancellationToken token)
    { try { await task; throw new InvalidOperationException("Expected cancellation"); } catch (OperationCanceledException ex) { Check(ex.CancellationToken == token, "original cancellation token"); } }
    private static void Clean() => Check(Fixture.Counter(0) == 0 && Fixture.Counter(2) == 0 && Fixture.Counter(4) == 0, "all creator owners/backings/capacity charges returned to zero");
    public static async Task Main(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported, "actual Native AOT execution");
        Clean(); ExtentOracle(); Copies(); AdoptionFailure(); RetainedSlices(); NativeFailures();
        await ReaderRace(); await AsyncSemantics(); await AsyncRetention(); await AsyncFailure();
        FinalizerFallback(); AllocationCost(); Clean();
        Console.WriteLine($"A04 PASS assertions={_assertions} owners={Fixture.Counter(0)} releases={Fixture.Counter(1)} backings={Fixture.Counter(2)} backing_releases={Fixture.Counter(3)} charged_capacity={Fixture.Counter(4)}");
    }
    private static bool Oracle(ulong address, ulong length, ulong count, ulong width, ulong offset, ulong capacity, ulong limit, uint alignment, out BigInteger start)
    {
        BigInteger a = address, n = length, c = count, w = width, o = offset, k = capacity, m = nuint.MaxValue;
        start = a + o;
        return w > 0 && alignment > 0 && (alignment & (alignment - 1)) == 0 && a <= m && k <= m && n <= (BigInteger)nint.MaxValue && n <= limit &&
            c * w == n && c * w <= ulong.MaxValue && o + n <= k && start <= m && start + n <= m && start % alignment == 0 &&
            (a != 0 || (n == 0 && o == 0));
    }
    private static void Compare(ulong a, ulong n, ulong c, ulong w, ulong o, ulong capacity, ulong limit, uint alignment)
    {
        bool expected = Oracle(a, n, c, w, o, capacity, limit, alignment, out BigInteger start);
        bool managed = ExtentValidator.TryValidate(a, n, c, w, o, capacity, limit, alignment, out nuint managedStart);
        bool native = Fixture.Validate(a, n, c, w, o, capacity, limit, alignment, out ulong nativeStart) == 0;
        Check(managed == expected && native == expected, "independent BigInteger/native/managed extent parity");
        Check(!expected || ((BigInteger)managedStart == start && nativeStart == (ulong)start), "exact checked native-width conversion");
    }
    private static void ExtentOracle()
    {
        ulong[] edges = [0, 1, 2, 7, 8, 15, 16, int.MaxValue, (ulong)int.MaxValue + 1, (ulong)nint.MaxValue, (ulong)nint.MaxValue + 1, ulong.MaxValue - 1, ulong.MaxValue];
        foreach (ulong n in edges) foreach (ulong w in edges)
        {
            Compare(8, n, n, w, 0, n, ulong.MaxValue, 1);
            Compare(8, n, 1, n, 0, n, ulong.MaxValue, 8);
            Compare(ulong.MaxValue - 8, n, n, 1, 8, ulong.MaxValue, ulong.MaxValue, 1);
            Compare(0, n, n, 1, 0, n, ulong.MaxValue, 1);
        }
        Compare(0, 0, 0, 1, 0, 0, 0, 1);
        Compare(0, 0, 0, 1, 0, 4096, 0, 1);
        Compare(8, 16, 4, 4, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 4);
        Compare(8, 16, 4, 4, 0, 16, 15, 4);
        Compare(7, 16, 4, 4, 0, 16, 16, 4);
        Compare(8, 16, 4, 4, 0, 16, 16, 3);
        var random = new Random(403004);
        for (int i = 0; i < 4096; i++)
        {
            ulong count = (ulong)random.Next(0, 4096), width = (ulong)random.Next(1, 64), length = count * width, offset = (ulong)random.Next(0, 64);
            ulong address = (ulong)random.Next(1, 65536), capacity = length + offset + (ulong)random.Next(0, 64);
            if (i % 4 == 0) length = edges[random.Next(edges.Length)];
            if (i % 7 == 0) offset = edges[random.Next(edges.Length)];
            if (i % 11 == 0) address = edges[random.Next(edges.Length)];
            Compare(address, length, count, width, offset, capacity, i % 5 == 0 ? length / 2 : ulong.MaxValue, (uint)(1 << random.Next(0, 4)));
        }
        Console.WriteLine("Extent oracle: 4778 descriptors; seed=403004; unbounded BigInteger arithmetic; no untrusted address dereference.");
    }
    private static void Copies()
    {
        using var buffer = Fixture.New([1, 2, 3, 4]);
        Check(buffer.Length == 4 && !buffer.IsDisposed, "immutable metadata");
        byte[] large = [9, 9, 9, 9, 9, 9]; buffer.CopyTo(large);
        Check(large.SequenceEqual(new byte[] { 1, 2, 3, 4, 9, 9 }), "copy leaves tail unchanged");
        byte[] shortDestination = [9, 9];
        Throws<ArgumentException>(() => buffer.CopyTo(shortDestination));
        Check(!buffer.TryCopyTo(shortDestination) && shortDestination.SequenceEqual(new byte[] { 9, 9 }), "short TryCopy leaves destination unchanged");
        byte[] range = new byte[2]; buffer.CopyTo(1, range); Check(range.SequenceEqual(new byte[] { 2, 3 }), "exact range");
        buffer.CopyTo(4, Span<byte>.Empty);
        Throws<ArgumentOutOfRangeException>(() => buffer.CopyTo(-1, range));
        Throws<ArgumentOutOfRangeException>(() => buffer.CopyTo(int.MaxValue, range));
        Throws<ArgumentOutOfRangeException>(() => buffer.CopyTo(3, range));
        Check(buffer.Read(static bytes => bytes[0] + bytes[3]) == 5, "generic scalar projection");
        buffer.Read(static bytes => { if (bytes.Length != 4) throw new Exception(); });
        Throws<FormatException>(() => buffer.Read(static _ => throw new FormatException("mapper")));
        Throws<ArgumentNullException>(() => buffer.Read((NativeBufferReader)null!));
        Throws<ArgumentNullException>(() => buffer.Read<int>(null!));
        byte[] copy = buffer.ToArray(); buffer.Dispose(); buffer.Dispose();
        Check(buffer.Length == 4 && buffer.IsDisposed && copy.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "copied values independent after dispose");
        Throws<ObjectDisposedException>(() => buffer.TryCopyTo(shortDestination));
        Throws<ObjectDisposedException>(() => buffer.ToArray());
        Throws<ObjectDisposedException>(() => buffer.Read(static _ => { }));
        using var empty = Fixture.New([]); Check(empty.Length == 0 && empty.ToArray().Length == 0 && empty.TryCopyTo(Span<byte>.Empty), "null+zero view is valid without null span construction");
        empty.Read(static b => { if (!b.IsEmpty) throw new Exception(); });
        empty.Dispose(); Clean();
        Overlap();
    }
    private static unsafe void Overlap()
    {
        // Isolated trusted fixture with exclusive access: only this test creates
        // a mutable native alias; the public API exposes none. Span.CopyTo is memmove.
        BufferHandle handle = Fixture.Create([1, 2, 3, 4, 5, 6]);
        Check(Fixture.View(handle.DangerousGetHandle(), out nint address, out _, out _) == 0, "overlap test view");
        using NativeBuffer buffer = Fixture.Adopt(handle);
        buffer.CopyTo(0, new Span<byte>((byte*)address + 1, 5));
        Check(buffer.ToArray().SequenceEqual(new byte[] { 1, 1, 2, 3, 4, 5 }), "forward overlap");
        buffer.CopyTo(1, new Span<byte>((void*)address, 5));
        Check(buffer.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 5 }), "backward overlap");
        buffer.Dispose(); Clean();
    }
    private static void AdoptionFailure()
    {
        using BufferHandle handle = Fixture.Create([1, 2]);
        Check(Fixture.View(handle.DangerousGetHandle(), out nint address, out _, out _) == 0, "view before adoption");
        Throws<ArgumentNullException>(() => NativeBufferFactory.Adopt(null!, handle, address, 2));
        Throws<ArgumentNullException>(() => NativeBufferFactory.Adopt(Fixture.Engine, null!, address, 2));
        Throws<ArgumentOutOfRangeException>(() => NativeBufferFactory.Adopt(Fixture.Engine, handle, address, -1));
        Throws<ArgumentException>(() => NativeBufferFactory.Adopt(Fixture.Engine, handle, 0, 2));
        Throws<ArgumentException>(() => NativeBufferFactory.Adopt(Fixture.Engine, handle, (nint)(-1), 2));
        Check(!handle.IsClosed && Fixture.Counter(0) == 1, "all malformed adoption failures preserve caller ownership");
        handle.Dispose();
        Throws<ArgumentException>(() => NativeBufferFactory.Adopt(Fixture.Engine, handle, address, 2));
        using var invalid = new BufferHandle(0);
        Throws<ArgumentException>(() => NativeBufferFactory.Adopt(Fixture.Engine, invalid, 0, 0));
        Clean();
    }
    private static void RetainedSlices()
    {
        BufferHandle original = Fixture.Create([1, 2, 3, 4], 4092);
        Check(Fixture.Counter(4) >= 4096, "charge actual allocated capacity");
        ulong charge = Fixture.Counter(4);
        Check(Fixture.Slice(original.DangerousGetHandle(), 2, 1, out nint raw) == 0, "native retained slice");
        var sliceHandle = new BufferHandle(raw);
        using NativeBuffer slice = Fixture.Adopt(sliceHandle);
        Check(Fixture.Slice(original.DangerousGetHandle(), 0, 4, out nint cloneRaw) == 0, "native full-view clone");
        using NativeBuffer clone = Fixture.Adopt(new BufferHandle(cloneRaw));
        original.Dispose();
        Check(Fixture.Counter(4) == charge && Fixture.Counter(2) == 1, "one byte slice retains full backing charge");
        Check(slice.ToArray().SequenceEqual(new byte[] { 3 }), "slice survives source disposal");
        slice.Dispose();
        Check(Fixture.Counter(4) == charge && clone.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 }), "full clone keeps backing after slice release");
        clone.Dispose(); Clean();
    }
    private static unsafe void NativeFailures()
    {
        foreach (uint fail in new uint[] { 1, 2 })
        {
            byte value = 1; Check(Fixture.Copy(&value, 1, 0, fail, out nint output) == 5 && output == 0, "reserve/publication failure leaves null output"); Clean();
        }
        Check(Fixture.Copy(null, 1, 0, 0, out nint invalid) == 3 && invalid == 0, "nonempty null borrow rejected before native slice");
        Check(Fixture.Copy(null, ulong.MaxValue, 0, 0, out invalid) == 3 && invalid == 0, "unrepresentable input rejected without read");
        Throws<FormatException>(() => { using NativeBuffer output = Fixture.New([1, 2]); _ = output.Read<int>(static _ => throw new FormatException("unpublished mapper")); });
        Clean();
    }
    private static async Task ReaderRace()
    {
        var buffer = Fixture.New([1, 2, 3]);
        using var entered = new ManualResetEventSlim(); using var proceed = new ManualResetEventSlim();
        Task reader = Task.Run(() => buffer.Read(bytes => { entered.Set(); Check(proceed.Wait(TimeSpan.FromSeconds(10)), "reader resumes"); Check(bytes[2] == 3, "admitted span survives dispose/GC"); }));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "reader entered outside admission lock");
        buffer.Dispose(); GC.Collect(); GC.WaitForPendingFinalizers();
        Check(Fixture.Counter(0) == 1 && Fixture.Counter(4) >= 3, "live source charge during leased reader");
        Throws<ObjectDisposedException>(() => buffer.TryCopyTo(Span<byte>.Empty));
        proceed.Set(); await reader.WaitAsync(TimeSpan.FromSeconds(10)); Clean();
        var reentrant = Fixture.New([7]); reentrant.Read(bytes => { reentrant.Dispose(); Check(bytes[0] == 7, "reentrant Dispose retains existing read"); }); Clean();
    }
    private static async Task AsyncSemantics()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var buffer = Fixture.New([1, 2]); using var readOnly = new MemoryStream([9], false); using var destination = new ObservedStream();
        Throws<ArgumentNullException>(() => buffer.CopyToAsync(null!, cancellation.Token));
        Throws<ArgumentException>(() => buffer.CopyToAsync(readOnly, cancellation.Token));
        await Canceled(buffer.CopyToAsync(destination, cancellation.Token), cancellation.Token);
        Check(destination.Writes == 0, "precancel performs no staging/write");
        buffer.Dispose(); Throws<ObjectDisposedException>(() => buffer.CopyToAsync(destination, cancellation.Token));
        using var empty = Fixture.New([]);
        await Canceled(empty.CopyToAsync(destination, cancellation.Token), cancellation.Token);
        await empty.CopyToAsync(destination);
        Check(destination.Writes == 0, "empty source never writes");
        empty.Dispose(); Clean();
        byte[] bytes = Enumerable.Range(0, 131073).Select(i => (byte)i).ToArray();
        using var large = Fixture.New(bytes); using var copy = new ObservedStream();
        await large.CopyToAsync(copy);
        Check(copy.ToArray().SequenceEqual(bytes) && copy.Writes == 3 && copy.MaximumArrayLength == 65536, "three staged writes, exact 64KiB backing array bound");
        Check(copy.CanWrite, "destination remains open"); large.Dispose(); Clean();
    }
    private static async Task AsyncRetention()
    {
        var buffer = Fixture.New(new byte[70000]); using var stream = new ControlledStream(); using var cancellation = new CancellationTokenSource();
        Task copy = buffer.CopyToAsync(stream, cancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        buffer.Dispose(); cancellation.Cancel(); GC.Collect(); GC.WaitForPendingFinalizers();
        Check(!copy.IsCompleted && Fixture.Counter(0) == 1 && Fixture.Counter(4) >= 70000, "cancel and dispose cannot revoke source/staging while WriteAsync still uses them");
        Check(stream.Pending.Length == 65536 && stream.Pending.Span[0] == 0, "pending staging memory remains live");
        stream.Continue.TrySetResult(); await Canceled(copy, cancellation.Token);
        Check(stream.Length == 65536, "cancellation preserves already written bytes"); Clean();
    }
    private static async Task AsyncFailure()
    {
        var buffer = Fixture.New(new byte[70000]); using var destination = new ObservedStream { FailAtWrite = 2 };
        Task copy = buffer.CopyToAsync(destination); buffer.Dispose();
        try { await copy; throw new Exception("Expected write failure"); } catch (IOException) { Check(destination.Length == 65536, "write error follows partial output without rollback"); }
        Check(destination.CanWrite, "failure leaves destination open"); Clean();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon() { var buffer = Fixture.New([1]); return new WeakReference(buffer); }
    private static void FinalizerFallback()
    {
        ulong releases = Fixture.Counter(1); WeakReference weak = Abandon();
        for (int i = 0; i < 10 && Fixture.Counter(1) == releases; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!weak.IsAlive && Fixture.Counter(1) == releases + 1, "creator SafeHandle fallback releases abandoned buffer once"); Clean();
    }
    private static void AllocationCost()
    {
        using var buffer = Fixture.New([1, 2, 3, 4]); byte[] target = new byte[4];
        for (int i = 0; i < 64; i++) buffer.CopyTo(target);
        long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1024; i++) buffer.CopyTo(target);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "warmed copies allocate no lease/array"); Console.WriteLine("Synchronous CopyTo measured managed allocation bytes=" + allocated + " calls=1024");
        buffer.Dispose();
    }
}
internal class ObservedStream : MemoryStream
{
    internal int Writes, MaximumArrayLength, FailAtWrite;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Writes++;
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment)) MaximumArrayLength = Math.Max(MaximumArrayLength, segment.Array!.Length);
        if (Writes == FailAtWrite) return ValueTask.FromException(new IOException("controlled write fault"));
        return base.WriteAsync(buffer, cancellationToken);
    }
}
internal sealed class ControlledStream : ObservedStream
{
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ReadOnlyMemory<byte> Pending { get; private set; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Pending = buffer; Entered.TrySetResult(); await Continue.Task.ConfigureAwait(false);
        await base.WriteAsync(buffer, CancellationToken.None).ConfigureAwait(false); Pending = default;
    }
}
