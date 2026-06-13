using System.Globalization;

namespace Orbit.Domain.Common;

/// <summary>
/// A stable machine-readable error code paired with its English fallback message.
/// Codes are the contract clients localize on; messages may contain {0}-style
/// placeholders resolved via <see cref="Format"/>.
/// </summary>
public sealed record AppError(string Code, string Message)
{
    public AppError Format(params object?[] args) =>
        this with { Message = string.Format(CultureInfo.InvariantCulture, Message, args) };
}
