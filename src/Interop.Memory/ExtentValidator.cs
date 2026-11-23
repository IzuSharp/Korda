namespace Korda.Interop.Memory;

/// <summary>Internal D07 contiguous-region arithmetic; it never probes or dereferences an address.</summary>
internal static class ExtentValidator
{
    internal static bool TryValidate(ulong address, ulong length, ulong count, ulong width,
        ulong offset, ulong capacity, ulong limit, uint alignment, out nuint start)
    {
        start = 0;
        ulong pointerMax = nuint.MaxValue;
        ulong signedMax = (ulong)nint.MaxValue;
        if (width == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0 ||
            address > pointerMax || length > signedMax || capacity > pointerMax ||
            offset > capacity || length > capacity - offset || length > limit ||
            count > ulong.MaxValue / width || count * width != length ||
            offset > pointerMax - address)
            return false;
        ulong first = address + offset;
        if (length > pointerMax - first || first % alignment != 0 ||
            (address == 0 && (length != 0 || offset != 0)))
            return false;
        start = (nuint)first;
        return true;
    }
}
