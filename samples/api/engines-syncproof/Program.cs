using System.Runtime.CompilerServices;
using System.Text;
using NativeWeave.Engines.SyncProof;
using NativeWeave.Interop;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Native AOT execution required.");
var codec = new ZlibCodec();
byte[] input = Encoding.UTF8.GetBytes("NativeWeave real native codec: 世界 🦀");
byte[] encoded = codec.Compress(input);
if (!codec.Decompress(encoded).SequenceEqual(input)) throw new InvalidOperationException("Round-trip failed.");

var bounded = new ZlibCodec(rawInputLimit: 1024, compressedInputLimit: 2048, decodedOutputLimit: 1024);
using NativeBuffer native = bounded.CompressOwned(input);
int encodedLength = native.Read(static bytes => bytes.Length);
using NativeBuffer decoded = bounded.DecompressOwned(native.ToArray());
if (!decoded.Read(bytes => bytes.SequenceEqual(input))) throw new InvalidOperationException("Scoped buffer comparison failed.");
Console.WriteLine($"PASS native zlib array and scoped-buffer consumers: {input.Length} bytes -> {encodedLength} bytes.");
