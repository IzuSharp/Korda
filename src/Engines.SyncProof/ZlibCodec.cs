using System.Runtime.InteropServices;
using NativeWeave.Engines.SyncProof.Generated;
using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;

namespace NativeWeave.Engines.SyncProof;

/// <summary>A bounded synchronous integration proof using the native miniz_oxide zlib algorithm.</summary>
/// <remarks>
/// This verification engine uses independent raw-input, compressed-input and decoded-output limits.
/// It performs no background work, callback, cancellation or executor startup. Instances contain only
/// immutable limits and are safe for concurrent calls on independently owned inputs. The package's
/// current native asset is Linux x64. This is an integration proof, not a general streaming codec API.
/// Native owners use the creating allocator and the shared Core buffer lifetime contract.
/// </remarks>
public sealed class ZlibCodec
{
    /// <summary>The maximum value of each independently enforced profile limit, in bytes.</summary>
    public const int MaximumProfileBytes = 16 * 1024 * 1024;
    private const string EngineId = "nativeweave.engines.syncproof";
    private static readonly Lazy<EngineDescriptor> Package = new(CreateDescriptor);
    private readonly NwCodecLimitsV1 _limits;

    /// <summary>Creates an immutable codec profile without loading native code or starting threads.</summary>
    /// <param name="rawInputLimit">Maximum bytes admitted for compression, from zero through MaximumProfileBytes.</param>
    /// <param name="compressedInputLimit">Maximum encoded bytes accepted by decompression and published by compression.</param>
    /// <param name="decodedOutputLimit">Maximum decoded bytes; must be at least rawInputLimit for same-profile round trips.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is negative, exceeds MaximumProfileBytes, or rawInputLimit exceeds decodedOutputLimit.</exception>
    /// <remarks>Defaults are 4 MiB raw/decoded and 4 MiB + 64 KiB encoded. A smaller encoded cap may reject incompressible input; it never publishes a result unusable by this same decoder. A zero decoded cap admits an empty decoded payload only.</remarks>
    public ZlibCodec(int rawInputLimit = 4194304, int compressedInputLimit = 4259840, int decodedOutputLimit = 4194304)
    {
        if ((uint)rawInputLimit > MaximumProfileBytes) throw new ArgumentOutOfRangeException(nameof(rawInputLimit));
        if ((uint)compressedInputLimit > MaximumProfileBytes) throw new ArgumentOutOfRangeException(nameof(compressedInputLimit));
        if ((uint)decodedOutputLimit > MaximumProfileBytes) throw new ArgumentOutOfRangeException(nameof(decodedOutputLimit));
        if (rawInputLimit > decodedOutputLimit) throw new ArgumentOutOfRangeException(nameof(rawInputLimit), "Raw input must fit this profile's decoded output limit.");
        _limits = new() { struct_size = 32, raw_limit = (ulong)rawInputLimit, encoded_limit = (ulong)compressedInputLimit, decoded_limit = (ulong)decodedOutputLimit };
    }

    /// <summary>Gets the statically compiled package descriptor without loading the native module.</summary>
    /// <remarks>First descriptor access reads a small assembly resource and allocates immutable metadata; later access reuses it. Inspect performs the shared A02 preflight. The module remains resident after load.</remarks>
    public static EngineDescriptor Descriptor => Package.Value;
    /// <summary>Gets the immutable compression-input byte limit with no allocation or native call.</summary>
    public int RawInputLimit => (int)_limits.raw_limit;
    /// <summary>Gets the immutable decoder-input and encoder-result byte limit with no allocation or native call.</summary>
    public int CompressedInputLimit => (int)_limits.encoded_limit;
    /// <summary>Gets the immutable decoded-output byte limit with no allocation or native call.</summary>
    public int DecodedOutputLimit => (int)_limits.decoded_limit;

    /// <summary>Compresses a synchronous borrowed span into a caller-owned managed array.</summary>
    /// <param name="input">Bytes borrowed only until the native compression call returns.</param>
    /// <returns>Independent zlib bytes admissible to this same profile's decoder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The raw input limit is exceeded before native admission.</exception>
    /// <exception cref="NativeEngineException">The encoded limit, native contract or panic boundary fails; no partial result is returned.</exception>
    /// <exception cref="EngineLoadException">The precompiled asset cannot pass preflight.</exception>
    /// <exception cref="OutOfMemoryException">A managed allocation or fallible native output reservation fails.</exception>
    /// <remarks>One synchronous native run and one native release; the owned output is copied once into an array. Compression is noninterruptible after entry. The Rust algorithm and fixed owner use some infallible allocations; process OOM/abort is not recoverable.</remarks>
    public byte[] Compress(ReadOnlySpan<byte> input)
    {
        using NativeBuffer output = CompressOwned(input);
        return output.ToArray();
    }

    /// <summary>Decompresses exactly one complete zlib stream into a caller-owned managed array.</summary>
    /// <param name="input">Encoded bytes borrowed only until the native call returns; trailing bytes are rejected.</param>
    /// <returns>Independent decoded bytes within DecodedOutputLimit.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The compressed input limit is exceeded before native admission.</exception>
    /// <exception cref="NativeEngineException">Input is malformed, truncated, corrupt, has trailing data, or exceeds the output limit; no partial bytes escape.</exception>
    /// <exception cref="EngineLoadException">The precompiled asset cannot pass preflight.</exception>
    /// <exception cref="OutOfMemoryException">A managed allocation or fallible native output reservation fails.</exception>
    /// <remarks>One native run/release pair and one output copy. Output growth uses fallible reservations capped by the declared limit. There is no cancellation point inside the synchronous algorithm.</remarks>
    public byte[] Decompress(ReadOnlySpan<byte> input)
    {
        using NativeBuffer output = DecompressOwned(input);
        return output.ToArray();
    }

    /// <summary>Compresses into a creator-owned immutable native buffer with scoped reads.</summary>
    /// <param name="input">Synchronous borrowed input, no longer retained when this method returns.</param>
    /// <returns>A buffer the caller must dispose; successful output can be decoded by this profile.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The raw input limit is exceeded.</exception>
    /// <exception cref="NativeEngineException">Native compression or its output limit fails without publishing an owner.</exception>
    /// <exception cref="EngineLoadException">The precompiled asset cannot pass preflight.</exception>
    /// <exception cref="OutOfMemoryException">Fallible reservation or managed wrapping fails; native ownership is released before propagation.</exception>
    /// <remarks>One native run; no managed payload copy. The result retains its actual reserved native capacity until the last Core read lease ends. Dispose adds one creator release. No raw pointer or escaping span is exposed.</remarks>
    public NativeBuffer CompressOwned(ReadOnlySpan<byte> input) => Run(input, 0, RawInputLimit);

    /// <summary>Decodes one complete zlib stream into a creator-owned immutable native buffer.</summary>
    /// <param name="input">Call-borrowed encoded input, including its header and checksum; trailing bytes are rejected.</param>
    /// <returns>A buffer that must be disposed, including when its length is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The encoded input limit is exceeded.</exception>
    /// <exception cref="NativeEngineException">Malformed input, checksum failure or decoded-output overrun is observed; partial output is discarded.</exception>
    /// <exception cref="EngineLoadException">The precompiled asset cannot pass preflight.</exception>
    /// <exception cref="OutOfMemoryException">Fallible reservation or managed wrapping fails; no native owner leaks.</exception>
    /// <remarks>One native run with bounded output growth and no managed payload copy. Scoped Read and CopyTo use A03/A04 leases. Disposal is nonblocking and supports already-admitted reads; there is no asynchronous codec operation.</remarks>
    public NativeBuffer DecompressOwned(ReadOnlySpan<byte> input) => Run(input, 1, CompressedInputLimit);

    private unsafe NativeBuffer Run(ReadOnlySpan<byte> input, uint mode, int limit)
    {
        if (input.Length > limit) throw new ArgumentOutOfRangeException(nameof(input), "Input exceeds this codec profile's limit.");
        EngineInfo engine = Descriptor.Inspect();
        engine.Require(new EngineCapability("codec.zlib.v1"));
        NwCodecLimitsV1 limits = _limits;
        NwErrorInfoV1 error = new() { struct_size = 24 };
        nint owner = 0;
        byte* data = null;
        ulong length = 0;
        CodecHandle? handle = null;
        bool committed = false;
        try
        {
            uint status;
            fixed (byte* pointer = input)
                status = NativeMethods.nw_syncproof_run_v1(pointer, (ulong)input.Length, mode, &limits, &owner, &data, &length, &error);
            if (status != 0)
            {
                if (owner != 0 || data != null || length != 0) throw new NativeEngineException("Native codec failure published partial ownership.");
                if (status == 5) throw new OutOfMemoryException("The native codec output reservation failed.");
                throw new NativeEngineException(EngineId, mode == 0 ? "compress" : "decompress", status, error.domain, error.code, error.correlation_id,
                    error.code switch { 1 => "Input is not one complete valid zlib stream.", 2 or 3 => "Input exceeds the native codec profile.", 4 => "Decoded output exceeds the profile limit.", 5 => "Encoded output exceeds the same-profile decoder input limit.", _ => "The native codec boundary failed." });
            }
            if (owner == 0 || length > (mode == 0 ? _limits.encoded_limit : _limits.decoded_limit)) throw new NativeEngineException("Native codec returned an invalid result owner or length.");
            CodecConstructionProbe.BeforeManagedAdoption?.Invoke();
            handle = new CodecHandle(owner);
            owner = 0; // SafeHandle now has the only destruction obligation.
            NativeBuffer result = NativeBufferFactory.Adopt(engine, handle, (nint)data, checked((int)length));
            committed = true; // Shared Core owns the handle only after successful wrapper construction.
            return result;
        }
        finally
        {
            if (owner != 0) NativeMethods.nw_syncproof_release_v1(owner);
            if (!committed) handle?.Dispose();
        }
    }

    private static EngineDescriptor CreateDescriptor()
    {
        using Stream input = typeof(ZlibCodec).Assembly.GetManifestResourceStream("NativeWeave.Engines.SyncProof.native-sha256")
            ?? throw new InvalidOperationException("The package is missing its prebuilt native identity.");
        using var reader = new StreamReader(input);
        string hash = reader.ReadToEnd().Trim().ToLowerInvariant();
        return EnginePackage.CreateDescriptor(new EnginePackageManifest(EngineId, "NativeWeave.Engines.SyncProof", "0.1.0-proof.1",
            new AbiVersion(1, 0), "nw_syncproof_get_abi_v1", EngineThreadingModel.Synchronous,
            [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnativeweave_engine_syncproof.so", hash, "x86-64")],
            [new EngineCapabilityDefinition(new EngineCapability("codec.zlib.v1"), 0)]), typeof(ZlibCodec).Assembly);
    }

    private sealed class CodecHandle : SafeHandle
    {
        internal CodecHandle(nint address) : base(0, true) { SetHandle(address); }
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { NativeMethods.nw_syncproof_release_v1(handle); return true; }
    }
}

internal static class CodecConstructionProbe
{
    [ThreadStatic] internal static Action? BeforeManagedAdoption;
}
