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
            if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Method == "POST")
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;

                if (IsMcpUnauthenticatedMethod(body))
                {
                    await next();
                    return;
                }

                // For tool calls, require auth
                var authResult = await context.AuthenticateAsync();
                if (!authResult.Succeeded)
                {
                    var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? context.Request.Scheme;
                    var resourceUrl = $"{scheme}://{context.Request.Host}/.well-known/oauth-protected-resource";
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceUrl}\"";
                    return;
                }
                context.User = authResult.Principal!;

                if (TryGetMcpToolCall(body, out var toolName, out var requestId, out var operationId, out var operationFingerprint))
                {
                    if (string.Equals(toolName, "execute_agent_operation_v2", StringComparison.OrdinalIgnoreCase))
                    {
                        await next();
                        return;
                    }

                    var catalogService = context.RequestServices.GetRequiredService<IAgentCatalogService>();
                    var policyEvaluator = context.RequestServices.GetRequiredService<IAgentPolicyEvaluator>();
                    var auditService = context.RequestServices.GetRequiredService<IAgentAuditService>();
                    var capability = catalogService.GetCapabilityByMcpTool(toolName!);

                    if (capability is null)
                    {
                        await TryAuditLegacyMcpAsync(
                            auditService,
                            context,
                            toolName!,
                            null,
                            AgentPolicyDecisionStatus.Denied,
                            AgentOperationStatus.UnsupportedByPolicy,
                            "unsupported_by_policy",
                            body,
                            null,
                            null,
                            CancellationToken.None);

                        await WriteMcpPolicyErrorAsync(
                            context,
                            requestId,
                            "unsupported_by_policy",
                            null);
                        return;
                    }

                    var decision = policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
                        capability.Id,
                        context.User.GetUserId(),
                        AgentExecutionSurface.Mcp,
                        context.User.GetAgentAuthMethod(),
                        context.User.GetGrantedAgentScopes(),
                        operationId ?? toolName!,
                        $"{operationId ?? toolName} requested via MCP",
                        operationFingerprint,
                        IsReadOnlyCredential: context.User.IsReadOnlyCredential()));

                    if (decision.Status == AgentPolicyDecisionStatus.ConfirmationRequired)
                    {
                        await TryAuditLegacyMcpAsync(
                            auditService,
                            context,
                            operationId ?? toolName!,
                            capability,
                            AgentPolicyDecisionStatus.ConfirmationRequired,
                            AgentOperationStatus.PendingConfirmation,
                            decision.Reason,
                            body,
                            decision.ShadowStatus,
                            decision.ShadowReason,
                            CancellationToken.None);

                        await WriteMcpPolicyErrorAsync(
                            context,
                            requestId,
                            decision.Reason ?? "confirmation_required",
                            decision.PendingOperation?.Id);
                        return;
                    }

                    if (decision.Status == AgentPolicyDecisionStatus.Denied)
                    {
                        await TryAuditLegacyMcpAsync(
                            auditService,
                            context,
                            operationId ?? toolName!,
                            capability,
                            AgentPolicyDecisionStatus.Denied,
                            AgentOperationStatus.Denied,
                            decision.Reason,
                            body,
                            decision.ShadowStatus,
                            decision.ShadowReason,
                            CancellationToken.None);

                        await WriteMcpPolicyErrorAsync(
                            context,
                            requestId,
                            decision.Reason ?? "policy_denied",
                            null);
                        return;
                    }

                    try
                    {
                        await next();
                        await TryAuditLegacyMcpAsync(
                            auditService,
                            context,
                            operationId ?? toolName!,
                            capability,
                            AgentPolicyDecisionStatus.Allowed,
                            AgentOperationStatus.Succeeded,
                            null,
                            body,
                            decision.ShadowStatus,
                            decision.ShadowReason,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        await TryAuditLegacyMcpAsync(
                            auditService,
                            context,
                            operationId ?? toolName!,
                            capability,
                            AgentPolicyDecisionStatus.Allowed,
                            AgentOperationStatus.Failed,
                            ex.Message,
                            body,
                            decision.ShadowStatus,
                            decision.ShadowReason,
                            CancellationToken.None);
                        throw;
                    }

                    return;
                }
            }
            await next();
        });
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
        string sourceName,
        AgentCapability? capability,
        AgentPolicyDecisionStatus policyDecision,
        AgentOperationStatus outcomeStatus,
        string? error,
        string rawBody,
        AgentPolicyDecisionStatus? shadowPolicyDecision,
        string? shadowReason,
        CancellationToken cancellationToken)
    {
        if (capability is null || context.User.Identity?.IsAuthenticated != true)
            return;

        try
        {
            var redactedArguments = rawBody.Length <= 1000 ? rawBody : rawBody[..1000];
            await auditService.RecordAsync(new AgentAuditEntry(
                context.User.GetUserId(),
                capability.Id,
                sourceName,
                AgentExecutionSurface.Mcp,
                context.User.GetAgentAuthMethod(),
                capability.RiskClass,
                policyDecision,
                outcomeStatus,
                context.TraceIdentifier,
                $"{sourceName} requested via MCP",
                RedactedArguments: redactedArguments,
                Error: error,
                ShadowPolicyDecision: shadowPolicyDecision,
                ShadowReason: shadowReason), cancellationToken);
        }
        catch
        {
            // Audit failures must not block MCP requests.
        }
    }
}
