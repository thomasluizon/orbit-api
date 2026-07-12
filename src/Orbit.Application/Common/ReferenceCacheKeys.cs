namespace Orbit.Application.Common;

/// <summary>
/// Cache keys and backstop TTL for per-user reference-data reads (tags, checklist templates, user
/// facts, API keys). Centralized so the read query and every mutation that invalidates it share a
/// single key definition and cannot drift apart.
/// </summary>
public static class ReferenceCacheKeys
{
    /// <summary>
    /// Backstop expiry for reference-data cache entries. Every known write path invalidates its key
    /// explicitly, so this only bounds staleness in the event a future write path forgets to; kept
    /// short for that safety margin.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static string Tags(Guid userId) => $"tags:{userId}";

    public static string ChecklistTemplates(Guid userId) => $"checklist-templates:{userId}";

    public static string UserFacts(Guid userId) => $"user-facts:{userId}";

    public static string ApiKeys(Guid userId) => $"api-keys:{userId}";
}
