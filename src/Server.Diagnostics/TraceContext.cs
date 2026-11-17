using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;
using NativeWeave.Server.Native;

namespace NativeWeave.Server;

/// <summary>Exact D19 fixed value; identifiers are W3C bytes and carry no ownership.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TraceContextV1
{
    internal uint Size;
    internal ushort Version;
    internal byte Flags;
    internal byte Reserved;
    internal fixed byte TraceId[16];
    internal fixed byte ParentSpanId[8];
}
internal static class DiagnosticContext
{
    internal static ActivityContext Decode(ReadOnlySpan<byte> bytes, string state)
    {
        if (bytes.Length != 32 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 32 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 || (bytes[6] & ~1) != 0 || bytes[7] != 0 || bytes[8..24].IndexOfAnyExcept((byte)0) < 0 || bytes[24..32].IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidOperationException("Malformed native D19 trace metadata.");
        if (!ValidState(state)) throw new InvalidOperationException("Malformed native trace state.");
        return new(ActivityTraceId.CreateFromBytes(bytes[8..24]), ActivitySpanId.CreateFromBytes(bytes[24..32]), (ActivityTraceFlags)bytes[6], state, true);
    }
    private static bool Key(ReadOnlySpan<char> text, bool firstAlpha, int maximum)
    {
        if (text.IsEmpty || text.Length > maximum || !(char.IsAsciiLetterLower(text[0]) || !firstAlpha && char.IsAsciiDigit(text[0]))) return false;
        foreach (char c in text[1..]) if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && !"_-*/".Contains(c)) return false;
        return true;
    }
    internal static bool ValidState(string text)
    {
        if (text.Length > 512 || text.Any(c => c > 127)) return false;
        HashSet<string> keys = new(StringComparer.Ordinal); int count = 0;
        foreach (Range item in text.AsSpan().Split(','))
        {
            if (++count > 32) return false;
            var member = text.AsSpan()[item].Trim(" \t"); if (member.IsEmpty) continue;
            int equals = member.IndexOf('='); if (equals <= 0) return false;
            var key = member[..equals]; var value = member[(equals + 1)..]; int at = key.IndexOf('@');
            if (!(at < 0 ? Key(key, true, 256) : Key(key[..at], false, 241) && Key(key[(at + 1)..], true, 14)) || !keys.Add(key.ToString())) return false;
            if (value.IsEmpty || value.Length > 256 || value[^1] == ' ') return false;
            foreach (char c in value) if (c < 32 || c > 126 || c is ',' or '=') return false;
        }
        return true;
    }
}
internal sealed partial class NativeRequest
{
    internal ActivityContext DiagnosticParent { get; private set; }
    private int _diagnosticCancellation;
    internal uint DiagnosticCancellation => (uint)Volatile.Read(ref _diagnosticCancellation);
    private void RecordCancellationDiagnostics(uint status) => Interlocked.CompareExchange(ref _diagnosticCancellation, checked((int)status), 0);
    internal unsafe void PopulateDiagnostics(NwBytesViewV1 trace, NwBytesViewV1 state)
    {
        if (trace.Length == 0)
        { if (state.Length != 0) throw new InvalidOperationException("Native trace state has no trace parent."); return; }
        if (trace.Length != 32 || trace.Data == null) throw new InvalidOperationException("Malformed native trace-context view.");
        DiagnosticParent = DiagnosticContext.Decode(new ReadOnlySpan<byte>(trace.Data, 32), NativeCodec.Text(state, 512));
        _traceIdentifier = DiagnosticParent.TraceId.ToHexString();
    }
    internal void UseDiagnosticTraceId(ActivityTraceId traceId) => _traceIdentifier = traceId.ToHexString();
}
