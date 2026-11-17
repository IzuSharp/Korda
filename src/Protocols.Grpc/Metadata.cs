using System.Buffers.Binary;
using System.Text;

namespace NativeWeave.Server.Grpc;

/// <summary>Stores bounded repeated ASCII text or copied binary gRPC metadata.</summary>
/// <remarks>At most 200 entries and 32 KiB including count/length prefixes and repeated names. Binary names end in -bin. Borrowed request collections are read-only and expire with the call. Response headers copy at admission; trailers freeze at terminal admission. Binary getters return caller-owned copies.</remarks>
public sealed class GrpcMetadata
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly List<IDisposable> _retained = [];
    private ReadOnlyMemory<byte> _borrowed;
    private Action? _alive;
    private Func<int, IDisposable>? _credit;
    private bool _readOnly, _frozen;
    private int _size = 4;
    private sealed record Entry(string Name, string? Text, byte[]? Binary);
    /// <summary>Creates an empty mutable metadata collection.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMetadata" path="/member/remarks" />
    public GrpcMetadata() { }
    internal GrpcMetadata(Action alive, Func<int, IDisposable> credit) { _alive = alive; _credit = credit; }
    internal static GrpcMetadata Borrow(byte[] bytes, Action alive, Func<int, IDisposable> credit)
    {
        ValidateBlock(bytes);
        return new(alive, credit) { _borrowed = bytes, _readOnly = true, _size = bytes.Length };
    }
    /// <summary>Appends one validated ASCII text value.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMetadata" path="/member/remarks" />
    /// <param name="name">A lowercase ASCII metadata name without the -bin suffix.</param>
    /// <param name="value">Printable ASCII text; empty values are retained.</param>
    public void Add(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value); Name(name, false);
        if (value.Any(c => c is < ' ' or > '~')) throw new ArgumentException("Metadata text must be printable ASCII.", nameof(value));
        lock (_gate) { Mutable(); int next = Size(name, value.Length); _entries.Add(new(name, value, null)); _size = next; }
    }
    /// <summary>Copies and appends one binary metadata value.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMetadata" path="/member/remarks" />
    /// <param name="name">A lowercase ASCII metadata name ending in -bin.</param>
    /// <param name="value">Raw bytes; no managed base64 conversion is performed.</param>
    public void AddBinary(string name, ReadOnlySpan<byte> value)
    {
        Name(name, true);
        lock (_gate)
        {
            Mutable(); int next = Size(name, value.Length); IDisposable? credit = _credit?.Invoke(Math.Max(1, value.Length));
            try { _entries.Add(new(name, null, value.ToArray())); if (credit is not null) _retained.Add(credit); credit = null; _size = next; }
            finally { credit?.Dispose(); }
        }
    }
    /// <summary>Gets repeated text values in per-name order.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMetadata" path="/member/remarks" />
    /// <param name="name">The lowercase non-binary metadata name.</param>
    /// <returns>An immutable list of caller-owned strings; missing names return an empty list.</returns>
    public IReadOnlyList<string> GetValues(string name)
    {
        Name(name, false);
        lock (_gate)
        {
            _alive?.Invoke(); using var credit = _credit?.Invoke(checked(_size * 2));
            if (!_readOnly) return Array.AsReadOnly(_entries.Where(e => e.Name == name).Select(e => e.Text!).ToArray());
            var values = new List<string>(); var span = _borrowed.Span; int offset = 4;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(span);
            for (uint i = 0; i < count; i++)
            { var pair = Read(span, ref offset); if (SameName(pair.Name, name)) values.Add(Encoding.ASCII.GetString(pair.Value)); }
            return values.AsReadOnly();
        }
    }
    /// <summary>Gets independent copies of repeated raw binary values.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMetadata" path="/member/remarks" />
    /// <param name="name">The lowercase metadata name ending in -bin.</param>
    /// <returns>An immutable list of caller-owned byte arrays, including distinct empty values.</returns>
    public IReadOnlyList<byte[]> GetBinaryValues(string name)
    {
        Name(name, true);
        lock (_gate)
        {
            _alive?.Invoke(); using var credit = _credit?.Invoke(_size);
            if (!_readOnly) return Array.AsReadOnly(_entries.Where(e => e.Name == name).Select(e => e.Binary!.ToArray()).ToArray());
            var values = new List<byte[]>(); var span = _borrowed.Span; int offset = 4;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(span);
            for (uint i = 0; i < count; i++)
            { var pair = Read(span, ref offset); if (SameName(pair.Name, name)) values.Add(pair.Value.ToArray()); }
            return values.AsReadOnly();
        }
    }
    internal int EncodedSize { get { lock (_gate) { _alive?.Invoke(); return _size; } } }
    internal (byte[] Bytes, IDisposable Credit) AdmitSnapshot(Func<int, IDisposable> acquire, bool freeze = false)
    {
        lock (_gate)
        {
            _alive?.Invoke(); var credit = acquire(_size);
            try { return (Snapshot(freeze), credit); }
            catch { credit.Dispose(); throw; }
        }
    }
    internal byte[] Snapshot(bool freeze = false)
    {
        lock (_gate)
        {
            _alive?.Invoke(); if (freeze) _frozen = true;
            if (_readOnly) return _borrowed.ToArray();
            byte[] bytes = new byte[_size]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)_entries.Count)); int offset = 4;
            foreach (var entry in _entries)
            {
                int length = entry.Text?.Length ?? entry.Binary!.Length;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)entry.Name.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), checked((uint)length)); offset += 8;
                offset += Encoding.ASCII.GetBytes(entry.Name, bytes.AsSpan(offset));
                if (entry.Text is { } text) offset += Encoding.ASCII.GetBytes(text, bytes.AsSpan(offset));
                else { entry.Binary!.CopyTo(bytes, offset); offset += length; }
            }
            return bytes;
        }
    }
    internal void Freeze() { lock (_gate) { _alive?.Invoke(); _frozen = true; } }
    internal void Release()
    { lock (_gate) { _frozen = true; _borrowed = default; _entries.Clear(); foreach (var credit in _retained) credit.Dispose(); _retained.Clear(); } }
    private int Size(string name, int bytes)
    { int next = checked(_size + 8 + name.Length + bytes); if (_entries.Count >= 200 || next > 32_768) throw new ArgumentOutOfRangeException(nameof(bytes), "The metadata entry/byte budget is exhausted."); return next; }
    private void Mutable() { _alive?.Invoke(); if (_readOnly || _frozen) throw new InvalidOperationException("Metadata is read-only or has frozen."); }
    private static void Name(string name, bool binary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 32_768 || name.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')) || name.EndsWith("-bin", StringComparison.Ordinal) != binary)
            throw new ArgumentException("The metadata name or binary suffix is invalid.", nameof(name));
    }
    private static bool SameName(ReadOnlySpan<byte> bytes, string name)
    { if (bytes.Length != name.Length) return false; for (int i = 0; i < bytes.Length; i++) if (bytes[i] != name[i]) return false; return true; }
    private readonly ref struct Pair(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    { internal ReadOnlySpan<byte> Name { get; } = name; internal ReadOnlySpan<byte> Value { get; } = value; }
    private static Pair Read(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset > bytes.Length - 8) throw new InvalidOperationException("Truncated gRPC metadata.");
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]), v = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]); offset += 8;
        if (n == 0 || n > bytes.Length - offset || v > bytes.Length - offset - n) throw new InvalidOperationException("Invalid gRPC metadata lengths.");
        var result = new Pair(bytes.Slice(offset, checked((int)n)), bytes.Slice(offset + (int)n, checked((int)v))); offset += checked((int)(n + v)); return result;
    }
    private static void ValidateBlock(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 4 or > 32_768) throw new InvalidOperationException("Invalid gRPC metadata block budget.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes); if (count > 200) throw new InvalidOperationException("Invalid gRPC metadata count.");
        int offset = 4;
        for (uint i = 0; i < count; i++)
        {
            var pair = Read(bytes, ref offset);
            foreach (byte b in pair.Name) if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-' or (byte)'.')) throw new InvalidOperationException("Invalid native gRPC metadata name.");
            bool binary = pair.Name.EndsWith("-bin"u8);
            if (!binary) foreach (byte b in pair.Value) if (b is < 32 or > 126) throw new InvalidOperationException("Invalid native gRPC metadata text.");
        }
        if (offset != bytes.Length) throw new InvalidOperationException("Trailing gRPC metadata bytes.");
    }
}
