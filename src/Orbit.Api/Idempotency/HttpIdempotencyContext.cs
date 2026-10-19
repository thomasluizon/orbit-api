using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using Orbit.Application.Common;

namespace Orbit.Api.Idempotency;

/// <summary>
/// Uses an explicit <c>Idempotency-Key</c> or a validated MCP session and JSON-RPC request id,
/// together with the authenticated user. Sessionless MCP requests require an explicit key.
/// The SDK sets the server feature after session ownership validation:
/// https://github.com/modelcontextprotocol/csharp-sdk/blob/2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01/src/ModelContextProtocol.AspNetCore/StreamableHttpHandler.cs#L268
/// </summary>
public sealed class HttpIdempotencyContext(IHttpContextAccessor httpContextAccessor) : IIdempotencyContext
{
    public const string McpRequestKey = "McpIdempotencyKey";
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";
    private const int MaxKeyLength = 200;
    private readonly Dictionary<string, int> _nextOrdinalByType = new(StringComparer.Ordinal);
    private readonly object _ordinalLock = new();

    public int NextRequestOrdinal(string requestType)
    {
        lock (_ordinalLock)
        {
            _nextOrdinalByType.TryGetValue(requestType, out var ordinal);
            _nextOrdinalByType[requestType] = checked(ordinal + 1);
            return ordinal;
        }
    }

    private static string BuildMcpKey(HttpContext context, McpReplayIdentity identity, string explicitKey)
    {
        var sessionId = context.Features.Get<McpServer>()?.SessionId;
        if (explicitKey.Length > 0 && explicitKey.Length <= MaxKeyLength)
            return JsonSerializer.Serialize(new { identity.Credential, SessionId = sessionId, ExplicitKey = explicitKey, identity.Fingerprint });
        if (string.IsNullOrEmpty(sessionId) || identity.RequestId is null)
            return string.Empty;
        return JsonSerializer.Serialize(new { identity.Credential, SessionId = sessionId, identity.RequestId, identity.Fingerprint });
    }

    public bool TryGetRequestKey(out Guid userId, [NotNullWhen(true)] out string idempotencyKey)
    {
        userId = Guid.Empty;
        idempotencyKey = "";

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
            return false;

        var explicitKey = httpContext.Request.Headers[IdempotencyKeyHeaderName].ToString().Trim();
        var identity = httpContext.Items[McpRequestKey] as McpReplayIdentity;
        var key = identity is null ? explicitKey : BuildMcpKey(httpContext, identity, explicitKey);
        if (identity is not null && key.Length > 0)
            key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            return false;

        var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out userId))
            return false;

        idempotencyKey = key;
        return true;
    }
}

internal sealed record McpReplayIdentity(string Credential, string? RequestId, string Fingerprint);
