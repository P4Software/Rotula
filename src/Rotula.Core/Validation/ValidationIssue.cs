namespace Rotula.Core.Validation;

/// <summary>Severity of a <see cref="ValidationIssue"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>Output is still produced but may not look as intended.</summary>
    Warning,
    /// <summary>The element or document cannot be printed correctly.</summary>
    Error,
}

/// <summary>
/// A structured validation result. Core never carries UI text: the UI looks
/// <see cref="MessageKey"/> up in its resources and formats it with <see cref="Args"/>.
/// </summary>
public sealed class ValidationIssue : IEquatable<ValidationIssue>
{
    /// <summary>Creates an issue.</summary>
    public ValidationIssue(ValidationSeverity severity, string messageKey, string? elementId = null, params string[] args)
    {
        Severity = severity;
        MessageKey = messageKey;
        ElementId = elementId;
        Args = args;
    }

    /// <summary>Warning or error.</summary>
    public ValidationSeverity Severity { get; }

    /// <summary>Stable resource key, for example <c>Barcode.Ean13.CheckDigit</c>.</summary>
    public string MessageKey { get; }

    /// <summary>Id of the offending element, or null for document-level issues.</summary>
    public string? ElementId { get; }

    /// <summary>Positional format arguments for the localized message (culture-neutral strings).</summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>Value equality including the contents of <see cref="Args"/>.</summary>
    public bool Equals(ValidationIssue? other) =>
        other is not null && Severity == other.Severity && MessageKey == other.MessageKey
        && ElementId == other.ElementId && Args.SequenceEqual(other.Args);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ValidationIssue);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Severity, MessageKey, ElementId, Args.Count);

    /// <inheritdoc />
    public override string ToString() => $"{Severity} {MessageKey}({string.Join(", ", Args)})";
}
