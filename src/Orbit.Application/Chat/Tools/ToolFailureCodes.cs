namespace Orbit.Application.Chat.Tools;

/// <summary>
/// The lowercase codes a failed tool call carries to the model. Only the model reads them, so they have no
/// user-facing copy and sit outside <see cref="Orbit.Application.Common.ErrorCodes"/>.
/// </summary>
public static class ToolFailureCodes
{
    /// <summary>The model's arguments broke a rule of the call. The failure message names the rule.</summary>
    public const string InvalidArguments = "invalid_arguments";

    /// <summary>An unexpected fault ended the call. The model gets a generic message and Sentry gets the exception.</summary>
    public const string UnexpectedError = "unexpected_error";
}
