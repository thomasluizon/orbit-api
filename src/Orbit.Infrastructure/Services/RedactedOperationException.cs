namespace Orbit.Infrastructure.Services;

public sealed class RedactedOperationException(string exceptionType, string? stackTrace)
    : Exception("Agent operation failed.")
{
    public override string? StackTrace => stackTrace;
    public override string ToString() => $"{exceptionType}: {Message}{Environment.NewLine}{StackTrace}";
}
