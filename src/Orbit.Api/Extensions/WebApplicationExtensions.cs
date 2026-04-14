using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Scalar.AspNetCore;

namespace Orbit.Api.Extensions;

public static class WebApplicationExtensions
{
    public static async Task ConfigureOrbitPipeline(this WebApplication app)
    {
        // Apply Migrations
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            await db.Database.MigrateAsync();
        }

        // Security & Forwarded Headers
        app.UseMiddleware<Orbit.Api.Middleware.SecurityHeadersMiddleware>();
        app.UseForwardedHeaders(BuildForwardedHeadersOptions(app));

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.UseExceptionHandler();
        app.UseCors();
        app.UseCookiePolicy();

        if (app.Environment.IsProduction())
        {
            app.UseHttpsRedirection();
        }

        // MCP selective auth
        app.UseMcpSelectiveAuth();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapMcp("/mcp");

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var result = new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        data = e.Value.Data
                    })
                };
                await context.Response.WriteAsJsonAsync(result);
            }
        }).AllowAnonymous();
    }

    private static void UseMcpSelectiveAuth(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!IsMcpPostRequest(context))
            {
                await next();
                return;
            }

            await HandleMcpRequestAsync(context, next);
        });
    }

    private static bool IsMcpPostRequest(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/mcp") && HttpMethods.IsPost(context.Request.Method);
    }

    private static async Task HandleMcpRequestAsync(HttpContext context, Func<Task> next)
    {
        var body = await ReadBufferedRequestBodyAsync(context);
        if (IsMcpUnauthenticatedMethod(body))
        {
            await next();
            return;
        }

        if (!await TryAuthenticateMcpRequestAsync(context))
            return;

        if (!TryGetMcpToolCall(body, out var toolName, out var requestId, out var operationId, out var operationFingerprint))
        {
            await next();
            return;
        }

        await HandleMcpToolCallAsync(
            context,
            next,
            body,
            new McpToolCallRequest(toolName!, requestId, operationId, operationFingerprint));
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
        var resourceUrl = $"{scheme}://{context.Request.Host}/.well-known/oauth-protected-resource";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceUrl}\"";
        return false;
    }

    private static async Task HandleMcpToolCallAsync(
        HttpContext context,
        Func<Task> next,
        string body,
        McpToolCallRequest toolCall)
    {
        if (string.Equals(toolCall.ToolName, "execute_agent_operation_v2", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var services = ResolveMcpServices(context);
        var capability = services.CatalogService.GetCapabilityByMcpTool(toolCall.ToolName);
        if (capability is null)
        {
            await AuditAndWritePolicyErrorAsync(
                context,
                services.AuditService,
                new LegacyMcpAuditContext(
                    toolCall.ToolName,
                    null,
                    AgentPolicyDecisionStatus.Denied,
                    AgentOperationStatus.UnsupportedByPolicy,
                    "unsupported_by_policy",
                    body),
                toolCall.RequestId,
                "unsupported_by_policy",
                null);
            return;
        }

        var sourceName = toolCall.OperationId ?? toolCall.ToolName;
        var decision = services.PolicyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            capability.Id,
            context.User.GetUserId(),
            AgentExecutionSurface.Mcp,
            context.User.GetAgentAuthMethod(),
            context.User.GetGrantedAgentScopes(),
            sourceName,
            $"{sourceName} requested via MCP",
            toolCall.OperationFingerprint,
            IsReadOnlyCredential: context.User.IsReadOnlyCredential()));

        if (decision.Status == AgentPolicyDecisionStatus.ConfirmationRequired)
        {
            await AuditAndWritePolicyErrorAsync(
                context,
                services.AuditService,
                new LegacyMcpAuditContext(
                    sourceName,
                    capability,
                    AgentPolicyDecisionStatus.ConfirmationRequired,
                    AgentOperationStatus.PendingConfirmation,
                    decision.Reason,
                    body,
                    decision.ShadowStatus,
                    decision.ShadowReason),
                toolCall.RequestId,
                decision.Reason ?? "confirmation_required",
                decision.PendingOperation?.Id);
            return;
        }

        if (decision.Status == AgentPolicyDecisionStatus.Denied)
        {
            await AuditAndWritePolicyErrorAsync(
                context,
                services.AuditService,
                new LegacyMcpAuditContext(
                    sourceName,
                    capability,
                    AgentPolicyDecisionStatus.Denied,
                    AgentOperationStatus.Denied,
                    decision.Reason,
                    body,
                    decision.ShadowStatus,
                    decision.ShadowReason),
                toolCall.RequestId,
                decision.Reason ?? "policy_denied",
                null);
            return;
        }

        await ExecuteAuthorizedMcpToolCallAsync(
            context,
            next,
            services.AuditService,
            new LegacyMcpAuditContext(
                sourceName,
                capability,
                AgentPolicyDecisionStatus.Allowed,
                AgentOperationStatus.Succeeded,
                null,
                body,
                decision.ShadowStatus,
                decision.ShadowReason));
    }

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

    private static ForwardedHeadersOptions BuildForwardedHeadersOptions(WebApplication app)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };

        var knownProxies = app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        foreach (var proxy in knownProxies)
        {
            if (IPAddress.TryParse(proxy, out var parsedProxy))
                options.KnownProxies.Add(parsedProxy);
        }

        var knownNetworks = app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
        foreach (var network in knownNetworks)
        {
            var parts = network.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var prefixLength))
                continue;

            if (System.Net.IPNetwork.TryParse($"{prefix}/{prefixLength}", out var parsedNetwork))
                options.KnownIPNetworks.Add(parsedNetwork);
        }

        return options;
    }

    private static bool IsMcpUnauthenticatedMethod(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                return method is "initialize" or "ping"
                    || (method?.StartsWith("notifications/") == true);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Invalid JSON -- require auth
        }

        return false;
    }

    private static bool TryGetMcpToolCall(
        string body,
        out string? toolName,
        out JsonElement? requestId,
        out string? operationId,
        out string? operationFingerprint)
    {
        toolName = null;
        requestId = null;
        operationId = null;
        operationFingerprint = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var methodElement) ||
                !string.Equals(methodElement.GetString(), "tools/call", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (root.TryGetProperty("id", out var idElement))
                requestId = idElement.Clone();

            if (!root.TryGetProperty("params", out var paramsElement))
                return false;

            if (paramsElement.TryGetProperty("name", out var nameElement))
                toolName = nameElement.GetString();

            if (string.Equals(toolName, "execute_agent_operation_v2", StringComparison.OrdinalIgnoreCase))
            {
                if (paramsElement.TryGetProperty("operationId", out var operationIdElement))
                    operationId = operationIdElement.GetString();

                if (paramsElement.TryGetProperty("arguments", out var argumentsElement))
                    operationFingerprint = $"{operationId}:{argumentsElement.GetRawText()}";
                else
                    operationFingerprint = $"{operationId}:{{}}";
            }
            else
            {
                operationFingerprint = $"{toolName}:{paramsElement.GetRawText()}";
            }

            return !string.IsNullOrWhiteSpace(toolName);
        }
        catch (JsonException)
        {
            return false;
        }
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
            var redactedArguments = auditContext.RawBody.Length <= 1000
                ? auditContext.RawBody
                : auditContext.RawBody[..1000];
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
        catch
        {
            // Audit failures must not block MCP requests.
        }
    }

    private sealed record McpToolCallRequest(
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
