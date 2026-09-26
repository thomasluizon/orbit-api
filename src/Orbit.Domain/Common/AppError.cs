using System.Globalization;

namespace Orbit.Domain.Common;

/// <summary>
/// A stable machine-readable error code paired with its English fallback message.
/// Codes are the contract clients localize on; messages may contain {0}-style
/// placeholders resolved via <see cref="Format"/>.
/// </summary>
public sealed record AppError(string Code, string Message)
{
    public IReadOnlyList<object?> Args { get; init; } = Array.Empty<object?>();

    public AppError Format(params object?[] args) =>
        this with
        {
            Message = string.Format(CultureInfo.InvariantCulture, Message, args),
            Args = args,
        };

    public bool Equals(AppError? other) =>
        other is not null
        && Code == other.Code
        && Message == other.Message
        && Args.SequenceEqual(other.Args);

    /// <inheritdoc cref="Equals(AppError)"/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Code);
        hash.Add(Message);
        foreach (var arg in Args)
            hash.Add(arg);
        return hash.ToHashCode();
    }
}
