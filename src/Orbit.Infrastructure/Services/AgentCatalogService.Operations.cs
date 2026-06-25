using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

#pragma warning disable S107 // Declarative catalog builders mirror the record shapes they populate.
#pragma warning disable S1192 // Catalog definitions intentionally reuse product vocabulary and JSON schema literals.
#pragma warning disable CA1861 // Static catalog schemas are evaluated once at startup and are not hot-path allocations.

public partial class AgentCatalogService
{
    private List<AgentOperation> BuildOperations(IEnumerable<IAiTool> tools)
    {
        var responseSchema = CloneJson(new
        {
            type = "object",
            properties = new
            {
                success = new { type = "boolean" },
                entity_id = new { type = "string", nullable = true },
                entity_name = new { type = "string", nullable = true },
                error = new { type = "string", nullable = true },
                payload = new { type = "object", nullable = true }
            },
            required = new[] { "success" }
        });

        return tools
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(tool =>
            {
                var capability = GetCapabilityByChatTool(tool.Name)
                    ?? throw new InvalidOperationException($"Chat tool '{tool.Name}' is not mapped to an agent capability.");

                return new AgentOperation(
                    tool.Name,
                    ToDisplayName(tool.Name),
                    tool.Description,
                    capability.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    !tool.IsReadOnly,
                    true,
                    CloneJson(tool.GetParameterSchema()),
                    responseSchema);
            })
            .Concat(BuildDirectFlowOperations())
            .OrderBy(operation => operation.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<AgentOperation> BuildDirectFlowOperations()
    {
        return
        [
            .. EmailCodeAuthOperations(),
            .. SessionLifecycleOperations()
        ];
    }

    private static AgentOperation[] EmailCodeAuthOperations()
    {
        return
        [
            new AgentOperation(
                "send_auth_code",
                "Send Auth Code",
                "Send a sign-in verification code directly to an email address.",
                AgentCapabilityIds.AuthManage,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                true,
                false,
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        email = new { type = "string" },
                        language = new { type = "string", nullable = true }
                    },
                    required = new[] { "email" }
                }),
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        success = new { type = "boolean" }
                    },
                    required = new[] { "success" }
                })),
            new AgentOperation(
                "verify_auth_code",
                "Verify Auth Code",
                "Verify an emailed sign-in code and create a session for the direct client.",
                AgentCapabilityIds.AuthManage,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                true,
                false,
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        email = new { type = "string" },
                        code = new { type = "string" },
                        language = new { type = "string", nullable = true },
                        referral_code = new { type = "string", nullable = true }
                    },
                    required = new[] { "email", "code" }
                }),
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        access_token = new { type = "string" },
                        refresh_token = new { type = "string" },
                        user_id = new { type = "string" }
                    },
                    required = new[] { "access_token", "refresh_token", "user_id" }
                }))
        ];
    }

    private static AgentOperation[] SessionLifecycleOperations()
    {
        return
        [
            new AgentOperation(
                "exchange_google_auth",
                "Exchange Google Auth",
                "Exchange a Google access token for a direct Orbit session.",
                AgentCapabilityIds.AuthManage,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                true,
                false,
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        access_token = new { type = "string" },
                        language = new { type = "string", nullable = true },
                        google_access_token = new { type = "string", nullable = true },
                        google_refresh_token = new { type = "string", nullable = true },
                        referral_code = new { type = "string", nullable = true }
                    },
                    required = new[] { "access_token" }
                }),
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        access_token = new { type = "string" },
                        refresh_token = new { type = "string" },
                        user_id = new { type = "string" }
                    },
                    required = new[] { "access_token", "refresh_token", "user_id" }
                })),
            new AgentOperation(
                "refresh_auth_session",
                "Refresh Auth Session",
                "Exchange a refresh token for a new access and refresh token pair.",
                AgentCapabilityIds.AuthManage,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                true,
                false,
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        refresh_token = new { type = "string" }
                    },
                    required = new[] { "refresh_token" }
                }),
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        access_token = new { type = "string" },
                        refresh_token = new { type = "string" }
                    },
                    required = new[] { "access_token", "refresh_token" }
                })),
            new AgentOperation(
                "logout_auth_session",
                "Logout Auth Session",
                "Revoke a refresh token for the direct client session.",
                AgentCapabilityIds.AuthManage,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                true,
                false,
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        refresh_token = new { type = "string" }
                    },
                    required = new[] { "refresh_token" }
                }),
                CloneJson(new
                {
                    type = "object",
                    properties = new
                    {
                        success = new { type = "boolean" }
                    },
                    required = new[] { "success" }
                }))
        ];
    }
}

#pragma warning restore CA1861
#pragma warning restore S1192
#pragma warning restore S107
