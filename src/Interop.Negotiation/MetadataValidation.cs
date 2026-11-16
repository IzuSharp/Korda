using System.Globalization;
using System.Text;

namespace NativeWeave.Interop;

// The v1 cold-metadata limits are part of the pinned Interop contract.
internal static class MetadataValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Identifier(string? value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length is 0 or > 128 || value[0] is < 'a' or > 'z')
            throw new ArgumentException("Use a lower-case dotted ASCII identifier of 1–128 bytes.", parameter);
        bool separator = false;
        foreach (char character in value)
        {
            if (character is '.' or '-')
            {
                if (separator)
                    throw new ArgumentException("Identifier separators need nonempty components.", parameter);
                separator = true;
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
                separator = false;
            else
                throw new ArgumentException("Use only lower-case ASCII letters, digits, dots and hyphens.", parameter);
        }
        if (separator)
            throw new ArgumentException("An identifier cannot end with a separator.", parameter);
        return value;
    }

    internal static int Text(string? value, string parameter, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new ArgumentException("A nonempty value without NUL is required.", parameter);
        int length;
        try { length = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The value must contain valid Unicode scalar values.", parameter);
        }
        if (length > maximum)
            throw new ArgumentException($"The value exceeds the {maximum}-byte UTF-8 limit.", parameter);
        return length;
    }

    internal static string Rid(string? value, string parameter)
    {
        Text(value, parameter, 128);
        // These are the two designed ABI-v1 profiles. An asset entry is metadata,
        // not evidence that that platform has passed product acceptance.
        if (value is not "linux-x64" and not "win-x64")
            throw new ArgumentException("ABI v1 uses the portable linux-x64 or win-x64 RID.", parameter);
        return value;
    }

    internal static string RelativePath(string? value, string parameter)
    {
        Text(value, parameter, 4096);
        if (value![0] == '/' || value.Contains('\\') || value.Contains(':'))
            throw new ArgumentException("Use a canonical relative path with forward slashes.", parameter);
        foreach (string segment in value.Split('/'))
            if (segment is "" or "." or "..")
                throw new ArgumentException("Paths cannot contain empty or traversal segments.", parameter);
        return value;
    }

    internal static string Hash(string? value, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Use the exact 64-character lowercase SHA-256.", parameter);
        return value;
    }

    internal static void Package(string packageId, string packageVersion)
    {
        Text(packageId, nameof(packageId), 256);
        if (packageId.Any(static c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new ArgumentException("Use a NuGet package identifier.", nameof(packageId));
        Text(packageVersion, nameof(packageVersion), 256);
        string[] metadata = packageVersion.Split('+');
        string[] prerelease = metadata[0].Split('-', 2);
        string[] numbers = prerelease[0].Split('.');
        if (metadata.Length > 2 || numbers.Length is < 3 or > 4)
            throw new ArgumentException("Use a normalized NuGet version string.", nameof(packageVersion));
        foreach (string number in numbers)
            if (number.Length == 0 || (number.Length > 1 && number[0] == '0') ||
                !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("Use normalized nonnegative version components.", nameof(packageVersion));
        if (numbers.Length == 4 && numbers[3] == "0")
            throw new ArgumentException("A normalized NuGet version omits a zero fourth component.", nameof(packageVersion));
        foreach (string suffix in prerelease.Skip(1).Concat(metadata.Skip(1)))
            foreach (string part in suffix.Split('.'))
                if (part.Length == 0 || part.Any(static c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
                    throw new ArgumentException("Use nonempty ASCII version labels.", nameof(packageVersion));
        if (prerelease.Length == 2)
            foreach (string part in prerelease[1].Split('.'))
                if (part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit))
                    throw new ArgumentException("Numeric prerelease labels cannot have leading zeros.", nameof(packageVersion));
    }

    internal static void Bootstrap(string value)
    {
        Text(value, "bootstrapSymbol", 128);
        if (!(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Use an exact C export identifier.", "bootstrapSymbol");
    }

    internal static void AddBytes(ref int total, string value, string parameter)
    {
        total = checked(total + StrictUtf8.GetByteCount(value));
        if (total > 65536)
            throw new ArgumentException("The manifest exceeds the 65,536-byte string budget.", parameter);
    }
}
