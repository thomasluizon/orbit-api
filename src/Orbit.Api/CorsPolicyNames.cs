namespace Orbit.Api;

/// <summary>
/// Strongly-typed CORS policy identifiers.
/// </summary>
/// <remarks>
/// We split CORS into two policies:
///   * <see cref="ApiPolicy"/> — credentialed (cookie + bearer) access from
///     the Orbit web/mobile frontends. Strict origin allow-list, never
///     includes claude.ai. Used for /api/* and OAuth endpoints.
///   * <see cref="McpPolicy"/> — bearer-token-only (no credentials) access
///     from claude.ai/com. Limited methods. Used only for /mcp.
/// Production startup validation enforces the host shape of each list (see
/// ValidateOrbitSecuritySettings).
/// </remarks>
public static class CorsPolicyNames
{
    public const string ApiPolicy = "ApiCors";
    public const string McpPolicy = "McpCors";

    public const string ApiOriginsConfigKey = "Cors:ApiOrigins";
    public const string McpOriginsConfigKey = "Cors:McpOrigins";
}
