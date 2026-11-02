using Orbit.Application.FeatureFlags;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Orbit.Api.Extensions;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Scalar.AspNetCore;

using Orbit.Application.Common;

namespace Orbit.Api.Mcp;

internal static partial class McpHttpGate
{
    private const string AgentOperationMcpTool = "execute_agent_operation_v2";
    internal const string McpRateLimitPolicy = "mcp";
    internal const string McpAiRateLimitPolicy = "mcp-ai";

    private static readonly HashSet<string> AiBearingMcpTools =
        new(StringComparer.OrdinalIgnoreCase) { "get_daily_summary", "get_goal_review", "get_retrospective" };

    private static readonly HashSet<string> AiBearingAgentOperations =
        new(StringComparer.OrdinalIgnoreCase) { "get_daily_summary", "get_retrospective", "review_goals" };

    internal static void Use(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp"))
            {
                await next();
                return;
            }

            if (!await TryAuthenticateMcpRequestAsync(context))
                return;

            if (HttpMethods.IsPost(context.Request.Method))
                await HandleMcpRequestAsync(context, next);
            else
                await next();
        });
    }

    private static async Task HandleMcpRequestAsync(HttpContext context, Func<Task> next)
    {
        var body = await ReadBufferedRequestBodyAsync(context);

        using var document = TryParseMcpBody(body);
        var root = document?.RootElement;

        if (root is { ValueKind: JsonValueKind.Object } message &&
            message.TryGetProperty("id", out var id) && !IsValidMcpRequestId(id))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = (int)McpErrorCode.InvalidRequest, message = "Invalid request ID" }
            }, context.RequestAborted);
            return;
        }

        if (IsMcpLifecycleMethod(root))
        {
            await next();
            return;
        }

        var isToolCall = TryGetMcpToolCall(
            root,
            out var toolName,
            out var requestId,
            out var operationId,
            out var operationFingerprint);

        if (!await TryApplyMcpRateLimitsAsync(context, toolName, operationId, requestId))
            return;

        if (!isToolCall)
        {
            if (root is { ValueKind: JsonValueKind.Object } discovery
                && discovery.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String && method.GetString() == "tools/list")
                await next();
            else
                await WriteMcpPolicyErrorAsync(context, requestId, Orbit.Application.Common.AgentPolicyReasons.UnsupportedByPolicy, null);
            return;
        }

        var moderation = context.RequestServices.GetRequiredService<IContentModerationService>();
        var check = await moderation.CheckTextAsync(body, context.RequestAborted);
        if (check.Flagged || check.Unavailable)
        {
            await WriteMcpPolicyErrorAsync(context, requestId,
                check.Unavailable ? "content_moderation_unavailable" : "content_moderation_refused", null);
            return;
        }

        var replayFingerprint = AgentOperationFingerprint.Compute(toolName!, root!.Value.GetProperty("params").GetRawText());
        context.Items[Orbit.Api.Idempotency.HttpIdempotencyContext.McpRequestKey] =
            new Orbit.Api.Idempotency.McpReplayIdentity(
                context.User.FindFirstValue("api_key_id") ?? context.User.GetUserId().ToString(),
                requestId?.GetRawText(), replayFingerprint);

        await HandleMcpToolCallAsync(
            context,
            next,
            body,
            new McpToolCallRequest(toolName!, requestId, operationId, operationFingerprint));
    }

    private static bool IsValidMcpRequestId(JsonElement id)
    {
        try
        {
            return id.Deserialize<RequestId>(McpJsonUtilities.DefaultOptions).Id is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static async Task<bool> TryApplyMcpRateLimitsAsync(
        HttpContext context,
        string? toolName,
        string? operationId,
        JsonElement? requestId)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return true;

        var apiKeyId = context.User.FindFirstValue("api_key_id");
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(apiKeyId) && string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("Authenticated MCP principal is missing a rate limit partition claim.");

        var partitionKey = !string.IsNullOrWhiteSpace(apiKeyId)
            ? $"api-key:{apiKeyId}"
            : $"user:{userId}";
        var service = context.RequestServices.GetRequiredService<IDistributedRateLimitService>();

        var decision = await service.TryAcquireAsync(
            McpRateLimitPolicy,
            partitionKey,
            context.RequestAborted);
        if (!decision.Allowed)
        {
            await WriteMcpRateLimitErrorAsync(
                context,
                requestId,
                McpRateLimitPolicy,
                partitionKey,
                decision);
            return false;
        }

        if (!IsAiBearingMcpCall(toolName, operationId))
            return true;

        var aiDecision = await service.TryAcquireAsync(
            McpAiRateLimitPolicy,
            partitionKey,
            context.RequestAborted);
        if (aiDecision.Allowed)
            return true;

        await WriteMcpRateLimitErrorAsync(
            context,
            requestId,
            McpAiRateLimitPolicy,
            partitionKey,
            aiDecision);
        return false;
    }

    private static bool IsAiBearingMcpCall(string? toolName, string? operationId)
    {
        if (toolName is null)
            return false;

        return AiBearingMcpTools.Contains(toolName)
            || (string.Equals(toolName, AgentOperationMcpTool, StringComparison.OrdinalIgnoreCase)
                && operationId is not null
                && AiBearingAgentOperations.Contains(operationId));
    }

    internal static JsonDocument? TryParseMcpBody(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadBufferedRequestBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        return body;
    }

    private static async Task<bool> TryAuthenticateMcpRequestAsync(HttpContext context)
    {
        var authResult = await context.AuthenticateAsync();
        if (authResult.Succeeded)
        {
            context.User = authResult.Principal!;
            return true;
        }

        var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? context.Request.Scheme;
        var resourceUrl = $"{scheme}://{context.Request.Host}/.well-known/oauth-protected-resource/mcp";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceUrl}\"";
        return false;
    }

    internal static async Task HandleMcpToolCallAsync(HttpContext context, Func<Task> next,
        string body, McpToolCallRequest toolCall)
    {
        if (string.Equals(toolCall.ToolName, AgentOperationMcpTool, StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolCall.ToolName, FeatureFlagActions.ToolName, StringComparison.Ordinal))
        {
            await next();
            return;
        }
        var services = ResolveMcpServices(context);
        var outcome = EvaluateToolPolicy(context, body, toolCall, services);
        if (outcome.Audit is null)
            await next();
        else if (outcome.Reason is not null)
            await AuditAndWritePolicyErrorAsync(context, services.AuditService, outcome.Audit,
                toolCall.RequestId, outcome.Reason, outcome.PendingOperationId);
        else
            await ExecuteAuthorizedMcpToolCallAsync(context, next, services.AuditService, outcome.Audit);
    }

    private static PolicyOutcome EvaluateToolPolicy(HttpContext context, string body,
        McpToolCallRequest toolCall, McpServices services)
    {
        var capability = services.CatalogService.GetCapabilityByMcpTool(toolCall.ToolName);
        if (capability is null)
            return new(new LegacyMcpAuditContext(toolCall.ToolName, null, AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.UnsupportedByPolicy, AgentPolicyReasons.UnsupportedByPolicy, body),
                AgentPolicyReasons.UnsupportedByPolicy);
        if (capability.ConfirmationRequirement is AgentConfirmationRequirement.FreshConfirmation or AgentConfirmationRequirement.StepUp)
            return new(null);
        var sourceName = toolCall.OperationId ?? toolCall.ToolName;
        var decision = services.PolicyEvaluator.Evaluate(new AgentPolicyEvaluationContext(capability.Id,
            context.User.GetUserId(), AgentExecutionSurface.Mcp, context.User.GetAgentAuthMethod(),
            context.User.GetGrantedAgentScopes(), sourceName, $"{sourceName} requested via MCP",
            toolCall.OperationFingerprint, IsReadOnlyCredential: context.User.IsReadOnlyCredential()));
        var operationStatus = decision.Status switch
        {
            AgentPolicyDecisionStatus.ConfirmationRequired => AgentOperationStatus.PendingConfirmation,
            AgentPolicyDecisionStatus.Denied => AgentOperationStatus.Denied,
            _ => AgentOperationStatus.Succeeded
        };
        var reason = decision.Status switch
        {
            AgentPolicyDecisionStatus.ConfirmationRequired => decision.Reason ?? "confirmation_required",
            AgentPolicyDecisionStatus.Denied => decision.Reason ?? "policy_denied",
            _ => null
        };
        var audit = new LegacyMcpAuditContext(sourceName, capability, decision.Status, operationStatus,
            decision.Status == AgentPolicyDecisionStatus.Allowed ? null : decision.Reason, body,
            decision.ShadowStatus, decision.ShadowReason);
        return new(audit, reason, decision.Status == AgentPolicyDecisionStatus.ConfirmationRequired
            ? decision.PendingOperation?.Id : null);
    }

    private sealed record PolicyOutcome(LegacyMcpAuditContext? Audit, string? Reason = null, Guid? PendingOperationId = null);

    private static McpServices ResolveMcpServices(HttpContext context)
    {
        return new McpServices(
            context.RequestServices.GetRequiredService<IAgentCatalogService>(),
            context.RequestServices.GetRequiredService<IAgentPolicyEvaluator>(),
            context.RequestServices.GetRequiredService<IAgentAuditService>());
    }

    private static async Task AuditAndWritePolicyErrorAsync(
        HttpContext context,
        IAgentAuditService auditService,
        LegacyMcpAuditContext auditContext,
        JsonElement? requestId,
        string reason,
        Guid? pendingOperationId)
    {
        await TryAuditLegacyMcpAsync(auditService, context, auditContext, CancellationToken.None);
        await WriteMcpPolicyErrorAsync(context, requestId, reason, pendingOperationId);
    }

    private static async Task ExecuteAuthorizedMcpToolCallAsync(
        HttpContext context,
        Func<Task> next,
        IAgentAuditService auditService,
        LegacyMcpAuditContext auditContext)
    {
        try
        {
            await next();
            await TryAuditLegacyMcpAsync(auditService, context, auditContext, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await TryAuditLegacyMcpAsync(
                auditService,
                context,
                auditContext with
                {
                    OutcomeStatus = AgentOperationStatus.Failed,
                    Error = ex.Message
                },
                CancellationToken.None);
            throw;
        }
    }

    internal static bool IsMcpLifecycleMethod(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var method = methodProp.GetString();
        return method is "initialize" or "ping"
            || method?.StartsWith("notifications/") == true;
    }

    internal static bool TryGetMcpToolCall(
        JsonElement? root,
        out string? toolName,
        out JsonElement? requestId,
        out string? operationId,
        out string? operationFingerprint)
    {
        toolName = null;
        requestId = null;
        operationId = null;
        operationFingerprint = null;

        if (root is not { ValueKind: JsonValueKind.Object } element)
            return false;

        JsonRpcMessage? message;
        try
        {
            message = element.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (message is not JsonRpcRequest request)
            return false;

        requestId = JsonSerializer.SerializeToElement(request.Id, McpJsonUtilities.DefaultOptions);
        if (!string.Equals(request.Method, RequestMethods.ToolsCall, StringComparison.Ordinal))
            return false;

        CallToolRequestParams? callParams;
        try
        {
            callParams = request.Params?.Deserialize<CallToolRequestParams>(McpJsonUtilities.DefaultOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (callParams is null || string.IsNullOrWhiteSpace(callParams.Name))
            return false;

        toolName = callParams.Name;
        var arguments = callParams.Arguments;
        if (string.Equals(toolName, AgentOperationMcpTool, StringComparison.OrdinalIgnoreCase))
        {
            if (arguments is not null
                && arguments.TryGetValue("operationId", out var operationIdElement)
                && operationIdElement.ValueKind == JsonValueKind.String)
            {
                operationId = operationIdElement.GetString();
            }

            operationFingerprint = arguments is not null
                && arguments.TryGetValue("arguments", out var argumentsElement)
                ? AgentOperationFingerprint.Compute(operationId ?? string.Empty, argumentsElement.GetRawText())
                : AgentOperationFingerprint.Compute(operationId ?? string.Empty, "{}");
        }
        else
        {
            operationFingerprint = AgentOperationFingerprint.Compute(
                toolName,
                arguments is null
                    ? "{}"
                    : JsonSerializer.Serialize(arguments, McpJsonUtilities.DefaultOptions));
        }

        return true;
    }

    private static async Task WriteMcpPolicyErrorAsync(
        HttpContext context,
        JsonElement? requestId,
        string reason,
        Guid? pendingOperationId)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            jsonrpc = "2.0",
            id = requestId,
            error = new
            {
                code = -32001,
                message = reason,
                data = new
                {
                    reason,
                    pendingOperationId
                }
            }
        });
    }

    private static async Task WriteMcpRateLimitErrorAsync(
        HttpContext context,
        JsonElement? requestId,
        string policyName,
        string partitionKey,
        DistributedRateLimitDecision decision)
    {
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var retryAfterSeconds = Math.Max(
            1,
            (int)Math.Ceiling((decision.WindowEndsAtUtc - now).TotalSeconds));

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers[Orbit.Api.Extensions.HttpContextExtensions.RequestIdHeaderName] = context.GetRequestId();

        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WebApplicationExtensions));
        LogMcpRateLimitRejected(
            logger,
            policyName,
            partitionKey,
            decision.CurrentCount,
            decision.PermitLimit,
            retryAfterSeconds,
            context.GetRequestId());

        await context.Response.WriteAsJsonAsync(new
        {
            jsonrpc = "2.0",
            id = requestId,
            error = new
            {
                code = -32002,
                message = "rate_limit_exceeded",
                data = new
                {
                    reason = "rate_limit_exceeded",
                    policy = policyName,
                    limit = decision.PermitLimit,
                    count = decision.CurrentCount,
                    retryAfterUtc = decision.WindowEndsAtUtc
                }
            }
        }, context.RequestAborted);
    }

    private static async Task TryAuditLegacyMcpAsync(
        IAgentAuditService auditService,
        HttpContext context,
        LegacyMcpAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        if (auditContext.Capability is null || context.User.Identity?.IsAuthenticated != true)
            return;

        try
        {
            var redactedArguments = AgentAuditRedactor.Redact(auditContext.RawBody);
            await auditService.RecordAsync(new AgentAuditEntry(
                context.User.GetUserId(),
                auditContext.Capability.Id,
                auditContext.SourceName,
                AgentExecutionSurface.Mcp,
                context.User.GetAgentAuthMethod(),
                auditContext.Capability.RiskClass,
                auditContext.PolicyDecision,
                auditContext.OutcomeStatus,
                context.TraceIdentifier,
                $"{auditContext.SourceName} requested via MCP",
                RedactedArguments: redactedArguments,
                Error: auditContext.Error,
                ShadowPolicyDecision: auditContext.ShadowPolicyDecision,
                ShadowReason: auditContext.ShadowReason), cancellationToken);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(WebApplicationExtensions));
            LogLegacyMcpAuditWriteFailed(logger, ex, auditContext.SourceName, context.TraceIdentifier);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to write MCP audit entry for {SourceName}. TraceId={TraceId}")]
    private static partial void LogLegacyMcpAuditWriteFailed(ILogger logger, Exception ex, string sourceName, string traceId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "MCP rate limit rejected. Policy={PolicyName} PartitionKey={PartitionKey} Count={CurrentCount}/{PermitLimit} RetryAfterSeconds={RetryAfterSeconds} RequestId={RequestId}")]
    private static partial void LogMcpRateLimitRejected(
        ILogger logger,
        string policyName,
        string partitionKey,
        int currentCount,
        int permitLimit,
        int retryAfterSeconds,
        string requestId);

    internal sealed record McpToolCallRequest(
        string ToolName,
        JsonElement? RequestId,
        string? OperationId,
        string? OperationFingerprint);

    private sealed record McpServices(
        IAgentCatalogService CatalogService,
        IAgentPolicyEvaluator PolicyEvaluator,
        IAgentAuditService AuditService);

    private sealed record LegacyMcpAuditContext(
        string SourceName,
        AgentCapability? Capability,
        AgentPolicyDecisionStatus PolicyDecision,
        AgentOperationStatus OutcomeStatus,
        string? Error,
        string RawBody,
        AgentPolicyDecisionStatus? ShadowPolicyDecision = null,
        string? ShadowReason = null);
}
