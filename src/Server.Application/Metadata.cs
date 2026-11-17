using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace NativeWeave.Server;

/// <summary>Bounded case-insensitive HTTP headers preserving repeated field values.</summary>
/// <remarks>Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
public sealed class HeaderCollection
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _alive;
    private readonly Action? _mutable;
    private readonly int _maximum;
    private readonly bool _trailers;
    internal HeaderCollection(Action alive, Action? mutable, int maximum, bool trailers = false, IReadOnlyDictionary<string, IReadOnlyList<string>>? values = null)
    {
        _alive = alive; _mutable = mutable; _maximum = maximum; _trailers = trailers;
        if (values is not null) foreach (var pair in values) _values.Add(pair.Key, [.. pair.Value]);
    }
    /// <summary>Gets the number of distinct names while the request is alive.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    public int Count { get { _alive(); return _values.Count; } }
    /// <summary>Gets all field values; missing names return an empty immutable list.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    public IReadOnlyList<string> this[string name] { get { _alive(); ValidateToken(name); return _values.TryGetValue(name, out var values) ? values.AsReadOnly() : []; } }
    /// <summary>Gets a single value or null; repeated input is rejected without comma folding.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public string? GetSingle(string name)
    {
        var values = this[name];
        return values.Count switch { 0 => null, 1 => values[0], _ => throw (_mutable is null
            ? new RequestBindingException(name, "Exactly one header value is required.") : new InvalidOperationException("The response header has repeated values.")) };
    }
    /// <summary>Tests for a field name.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public bool Contains(string name) { _alive(); ValidateToken(name); return _values.ContainsKey(name); }
    /// <summary>Replaces response values before commit; request instances are read-only.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <param name="value">The value value described by this operation.</param>
    public void Set(string name, string value) => Change(name, value, false);
    /// <summary>Appends one response field without combining repeated values.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <param name="value">The value value described by this operation.</param>
    public void Append(string name, string value) => Change(name, value, true);
    /// <summary>Removes a mutable field before commit.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public bool Remove(string name) { Writable(); ValidateName(name); return _values.Remove(name); }
    /// <summary>Copies a bounded caller-owned metadata snapshot.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.HeaderCollection" path="/member/remarks" />
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ToDictionary()
    { _alive(); return _values.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)Array.AsReadOnly(p.Value.ToArray()), StringComparer.OrdinalIgnoreCase); }
    internal void Clear() { Writable(); _values.Clear(); }
    private void Change(string name, string value, bool append)
    {
        Writable(); ValidateName(name); ValidateValue(value);
        if (!_values.ContainsKey(name) && _values.Count >= 200) throw new InvalidOperationException("Header field count exceeded.");
        long bytes = _values.Sum(p => (long)Encoding.UTF8.GetByteCount(p.Key) + p.Value.Sum(v => (long)Encoding.UTF8.GetByteCount(v)));
        if (_values.TryGetValue(name, out var previous) && !append) bytes -= previous.Sum(v => (long)Encoding.UTF8.GetByteCount(v));
        if (previous is null) bytes += Encoding.UTF8.GetByteCount(name);
        bytes += Encoding.UTF8.GetByteCount(value);
        if (bytes > _maximum) throw new InvalidOperationException("Header byte budget exceeded.");
        if (!append || previous is null) _values[name] = [value]; else previous.Add(value);
    }
    private void Writable() { _alive(); if (_mutable is null) throw new InvalidOperationException("Request headers are read-only."); _mutable(); }
    private void ValidateName(string name)
    {
        ValidateToken(name);
        if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
            (_trailers && (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Transport-owned or forbidden framing field.", nameof(name));
    }
    internal static void ValidateToken(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))) throw new ArgumentException("Invalid HTTP token.", nameof(name));
    }
    internal static void ValidateValue(string value)
    { ArgumentNullException.ThrowIfNull(value); if (value.Any(c => c is '\r' or '\n' or '\0' || c < ' ' && c != '\t' || c == '\u007f')) throw new ArgumentException("Invalid HTTP field value.", nameof(value)); }
}

/// <summary>Invariant typed access to already decoded native route/query values, without reflection.</summary>
/// <remarks>Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
public sealed class ParameterValues
{
    private readonly Action _alive;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _values;
    internal ParameterValues(Action alive, IReadOnlyDictionary<string, IReadOnlyList<string>> values) { _alive = alive; _values = values; }
    /// <summary>Parses exactly one required value or throws a bounded binding error.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ParameterValues" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <typeparam name="T">The statically known result type; JSON paths require explicit or registered static metadata.</typeparam>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public T Get<T>(string name) where T : IParsable<T> => TryGet<T>(name, out var value) ? value : throw new RequestBindingException(name, "A required value is missing, repeated or malformed.");
    /// <summary>Parses one value; false means absent, repeated or malformed.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ParameterValues" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <param name="value">The value value described by this operation.</param>
    /// <typeparam name="T">The statically known result type; JSON paths require explicit or registered static metadata.</typeparam>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public bool TryGet<T>(string name, [MaybeNullWhen(false)] out T value) where T : IParsable<T>
    {
        var values = GetValues(name);
        if (values.Count == 1) return T.TryParse(values[0], CultureInfo.InvariantCulture, out value);
        value = default; return false;
    }
    /// <summary>Gets raw decoded values; returns an empty list when absent.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ParameterValues" path="/member/remarks" />
    /// <param name="name">The name value described by this operation.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public IReadOnlyList<string> GetValues(string name)
    { _alive(); ArgumentNullException.ThrowIfNull(name); return _values.TryGetValue(name, out var values) ? values : []; }
}

internal sealed class RequestItems(Action alive) : IDictionary<object, object?>
{
    private readonly Dictionary<object, object?> _values = [];
    public object? this[object key] { get { alive(); return _values[key]; } set { alive(); Capacity(key); _values[key] = value; } }
    public ICollection<object> Keys { get { alive(); return _values.Keys; } }
    public ICollection<object?> Values { get { alive(); return _values.Values; } }
    public int Count { get { alive(); return _values.Count; } }
    public bool IsReadOnly => false;
    public void Add(object key, object? value) { alive(); Capacity(key); _values.Add(key, value); }
    public void Add(KeyValuePair<object, object?> item) => Add(item.Key, item.Value);
    public void Clear() { alive(); _values.Clear(); }
    public bool Contains(KeyValuePair<object, object?> item) { alive(); return ((ICollection<KeyValuePair<object, object?>>)_values).Contains(item); }
    public bool ContainsKey(object key) { alive(); return _values.ContainsKey(key); }
    public void CopyTo(KeyValuePair<object, object?>[] array, int arrayIndex) { alive(); ((ICollection<KeyValuePair<object, object?>>)_values).CopyTo(array, arrayIndex); }
    public IEnumerator<KeyValuePair<object, object?>> GetEnumerator() { alive(); return _values.GetEnumerator(); }
    public bool Remove(object key) { alive(); return _values.Remove(key); }
    public bool Remove(KeyValuePair<object, object?> item) { alive(); return ((ICollection<KeyValuePair<object, object?>>)_values).Remove(item); }
    public bool TryGetValue(object key, out object? value) { alive(); return _values.TryGetValue(key, out value); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private void Capacity(object key) { if (!_values.ContainsKey(key) && _values.Count >= 128) throw new InvalidOperationException("Request Items is limited to 128 entries."); }
}
