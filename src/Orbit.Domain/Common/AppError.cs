using System.Globalization;

namespace Orbit.Domain.Common;

/// <summary>
/// A stable machine-readable error code paired with its English fallback message.
/// Codes are the contract clients localize on; messages may contain {0}-style
/// placeholders resolved via <see cref="Format"/>.
/// </summary>
public sealed record AppError(string Code, string Message)
{
    /// <summary>
    /// The arguments the last <see cref="Format"/> call applied, kept so the user-facing
    /// counterpart selected by <see cref="Code"/> can be formatted with the same values.
    /// <see cref="Format"/> bakes them into <see cref="Message"/> eagerly, which otherwise
    /// leaves the response boundary no way to recover them. The default is
    /// <see cref="Array.Empty{T}"/> rather than a fresh array so that two errors carrying
    /// no arguments stay equal under the record's structural equality.
    /// </summary>
    public IReadOnlyList<object?> Args { get; init; } = Array.Empty<object?>();

    public AppError Format(params object?[] args) =>
        this with
        {
            Message = string.Format(CultureInfo.InvariantCulture, Message, args),
            Args = args,
        };
}
