namespace Orbit.Application.Common;

public static class EvalAccountAccess
{
    public const string AccountIdsKey = "StagingEval:AccountIds";

    public static bool Contains(string? accountIds, Guid userId) => userId != Guid.Empty
        && (accountIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => Guid.TryParse(value, out var allowedId) && allowedId == userId);
}
