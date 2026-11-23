using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Korda.Data;

// Used only by the explicit bounded snapshot path. Ordinary B01 codec encoding
// stays unchanged. The supplied static JsonTypeInfo remains the sole serializer.
internal static class BoundedJsonEncoding
{
    internal static byte[] Encode<T>(T value, JsonTypeInfo<T> typeInfo, int maximumBytes)
    {
        var output = new FiniteBuffer(maximumBytes);
        var options = typeInfo.Options;
        var writer = new Utf8JsonWriter(output, new()
        {
            Encoder = new TokenBudgetEncoder(options.Encoder ?? JavaScriptEncoder.Default, maximumBytes),
            Indented = options.WriteIndented,
            IndentCharacter = options.IndentCharacter,
            IndentSize = options.IndentSize,
            NewLine = options.NewLine,
            MaxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth
        });
        try
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
            writer.Flush();
            return output.Copy();
        }
        catch
        {
            // Discard pending partial JSON so Dispose cannot replace a converter
            // exception by trying to flush an already rejected oversized value.
            try { writer.Reset(); } catch (ObjectDisposedException) { }
            throw;
        }
        finally { writer.Dispose(); }
    }

    // Utf8JsonWriter requests worst-case string space (up to six bytes per
    // character) and a 4096-byte growth hint. Logical Advance still enforces the
    // exact payload limit; scratch capacity never grows beyond this finite cap.
    private sealed class FiniteBuffer(int maximumBytes) : IBufferWriter<byte>
    {
        private readonly int _scratchLimit = checked(maximumBytes * 6 + 4096);
        private byte[] _buffer = [];
        private int _written;
        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > maximumBytes - _written) throw ValueCodec<byte>.Capacity();
            _written += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            sizeHint = Math.Max(1, sizeHint);
            if (sizeHint > _scratchLimit - _written) throw ValueCodec<byte>.Capacity();
            if (_buffer.Length - _written < sizeHint)
            {
                var capacity = Math.Max(_written + sizeHint, Math.Min(_scratchLimit, Math.Max(256, _buffer.Length * 2)));
                var next = new byte[capacity]; _buffer.AsSpan(0, _written).CopyTo(next); _buffer = next;
            }
            return _buffer.AsMemory(_written);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        internal byte[] Copy() => _buffer.AsSpan(0, _written).ToArray();
    }

    // .NET11 may rent the escape scratch buffer before calling GetMemory.
    // Guard both token forms before delegating the exact selected encoder, so
    // an enormous escaped string cannot bypass the finite output writer.
    private sealed class TokenBudgetEncoder(JavaScriptEncoder inner, int maximumBytes) : JavaScriptEncoder
    {
        public override int MaxOutputCharactersPerInputCharacter => inner.MaxOutputCharactersPerInputCharacter;
        public override bool WillEncode(int unicodeScalar) => inner.WillEncode(unicodeScalar);
        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            if (textLength > maximumBytes) throw ValueCodec<byte>.Capacity();
            return inner.FindFirstCharacterToEncode(text, textLength);
        }
        public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text)
        {
            if (utf8Text.Length > maximumBytes) throw ValueCodec<byte>.Capacity();
            return inner.FindFirstCharacterToEncodeUtf8(utf8Text);
        }
        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
            => inner.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);
    }
}
