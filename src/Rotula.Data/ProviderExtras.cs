using System.Collections;

namespace Rotula.Data;

/// <summary>
/// Provider-specific option values (key to text) that have no dedicated property on <see cref="DatabaseConnectionSettings"/>:
/// a region, an account name, a pooler switch, the underlying engine of an ODBC source. It is an immutable value type in spirit
/// (value equality, case-insensitive keys, ordered) so the settings record keeps working as a value. Keys are validated against
/// the provider's option schema before use; a hand-edited file cannot add unknown keys.
/// </summary>
public sealed class ProviderExtras : IReadOnlyDictionary<string, string>, IEquatable<ProviderExtras>
{
    /// <summary>Most entries one connection may carry.</summary>
    public const int MaxEntries = 32;

    /// <summary>Longest key and value, in characters.</summary>
    public const int MaxLength = 1024;

    private readonly SortedDictionary<string, string> _items;

    /// <summary>The empty set (the default).</summary>
    public static ProviderExtras Empty { get; } = new(Array.Empty<KeyValuePair<string, string>>());

    /// <summary>Creates a set from pairs; later duplicates (case-insensitive) win.</summary>
    public ProviderExtras(IEnumerable<KeyValuePair<string, string>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in items) _items[kv.Key] = kv.Value ?? string.Empty;
    }

    /// <summary>Creates a set from key/value tuples.</summary>
    public static ProviderExtras Of(params (string Key, string Value)[] pairs)
        => new(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));

    /// <summary>A copy with <paramref name="key"/> set (or removed when the value is null or empty).</summary>
    public ProviderExtras With(string key, string? value)
    {
        var copy = new Dictionary<string, string>(_items, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(value)) copy.Remove(key); else copy[key] = value;
        return new ProviderExtras(copy);
    }

    /// <summary>Value of <paramref name="key"/>, or null.</summary>
    public string? Get(string key) => _items.TryGetValue(key, out var v) ? v : null;

    /// <summary>True when the key holds a true-ish value (<c>true</c>, <c>1</c>, <c>yes</c>, <c>on</c>).</summary>
    public bool GetBool(string key) => Get(key) is { } v
        && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public string this[string key] => _items[key];
    /// <inheritdoc />
    public IEnumerable<string> Keys => _items.Keys;
    /// <inheritdoc />
    public IEnumerable<string> Values => _items.Values;
    /// <inheritdoc />
    public int Count => _items.Count;
    /// <inheritdoc />
    public bool ContainsKey(string key) => _items.ContainsKey(key);
    /// <inheritdoc />
    public bool TryGetValue(string key, out string value) => _items.TryGetValue(key, out value!);
    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(ProviderExtras? other)
        => other is not null && _items.Count == other._items.Count
           && _items.All(kv => other._items.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ProviderExtras);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var kv in _items) { h.Add(kv.Key, StringComparer.OrdinalIgnoreCase); h.Add(kv.Value); }
        return h.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => string.Join(", ", _items.Select(kv => kv.Key + "=" + kv.Value));
}
