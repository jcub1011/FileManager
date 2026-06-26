namespace FileManager.Core.Profiles;

/// <summary>A single, descriptive validation problem located by a dotted field path.</summary>
public sealed record ValidationError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>
/// The outcome of validating a document: an ordered list of <see cref="ValidationError"/>.
/// Validation never throws — problems are reported as data, per the milestone requirement.
/// </summary>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    /// <summary>True when there are no errors.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>A shared, empty (valid) result.</summary>
    public static ValidationResult Success { get; } = new(Array.Empty<ValidationError>());

    /// <summary>Builds a failed result from a single error.</summary>
    public static ValidationResult Fail(string path, string message) =>
        new(new[] { new ValidationError(path, message) });
}
