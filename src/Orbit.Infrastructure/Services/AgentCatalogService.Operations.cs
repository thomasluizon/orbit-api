using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public partial class AgentCatalogService
{
    private const string SchemaObject = "object";
    private const string SchemaString = "string";
    private const string SchemaBoolean = "boolean";
    private const string SuccessField = "success";
    private const string AccessTokenField = "access_token";
    private const string RefreshTokenField = "refresh_token";
    private static readonly string[] RequiredSuccess = [SuccessField];
    private static readonly string[] RequiredEmail = ["email"];
    private static readonly string[] RequiredEmailCode = ["email", "code"];
    private static readonly string[] RequiredAuthTokens = [AccessTokenField, RefreshTokenField, "user_id"];
    private static readonly string[] RequiredGoogleCode = ["code", "code_verifier", "redirect_uri"];
    private static readonly string[] RequiredRefreshToken = [RefreshTokenField];
    private static readonly string[] RequiredSessionTokens = [AccessTokenField, RefreshTokenField];

    private List<AgentOperation> BuildOperations(IEnumerable<IAiTool> tools)
    {
        var responseSchema = CloneJson(new
        {
            type = SchemaObject,
            properties = new
            {
                success = new { type = SchemaBoolean },
                entity_id = new { type = SchemaString, nullable = true },
                entity_name = new { type = SchemaString, nullable = true },
                error = new { type = SchemaString, nullable = true },
                payload = new { type = SchemaObject, nullable = true }
            },
            required = RequiredSuccess
        });

        return tools
            .Where(tool => tool is not IChatOnlyAiTool)
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
                    type = SchemaObject,
                    properties = new
                    {
                        email = new { type = SchemaString },
                        language = new { type = SchemaString, nullable = true }
                    },
                    required = RequiredEmail
                }),
                CloneJson(new
                {
                    type = SchemaObject,
                    properties = new
                    {
                        success = new { type = SchemaBoolean }
                    },
                    required = RequiredSuccess
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
                    type = SchemaObject,
                    properties = new
                    {
                        email = new { type = SchemaString },
                        code = new { type = SchemaString },
                        language = new { type = SchemaString, nullable = true },
                        referral_code = new { type = SchemaString, nullable = true }
                    },
                    required = RequiredEmailCode
                }),
                CloneJson(new
                {
                    type = SchemaObject,
                    properties = new
                    {
                        access_token = new { type = SchemaString },
                        refresh_token = new { type = SchemaString },
                        user_id = new { type = SchemaString }
                    },
                    required = RequiredAuthTokens
                }))
        ];
    }

    private static AgentOperation[] SessionLifecycleOperations()
    {
        return
        [
            new AgentOperation(
                "exchange_google_auth_code",
                "Exchange Google Auth Code",
                "Exchange a Google authorization code for a direct Orbit session.",
                AgentCapabilityIds.AuthManage,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                true,
                false,
                CloneJson(new
                {
                    type = SchemaObject,
                    properties = new
                    {
                        code = new { type = SchemaString },
                        code_verifier = new { type = SchemaString },
                        redirect_uri = new { type = SchemaString },
                        language = new { type = SchemaString, nullable = true },
                        referral_code = new { type = SchemaString, nullable = true }
                    },
                    required = RequiredGoogleCode
                }),
                CloneJson(new
                {
                    type = SchemaObject,
                    properties = new
                    {
                        access_token = new { type = SchemaString },
                        refresh_token = new { type = SchemaString },
                        user_id = new { type = SchemaString }
                    },
                    required = RequiredAuthTokens
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
                    type = SchemaObject,
                    properties = new
                    {
                        refresh_token = new { type = SchemaString }
                    },
                    required = RequiredRefreshToken
                }),
                CloneJson(new
                {
                    type = SchemaObject,
                    properties = new
                    {
                        access_token = new { type = SchemaString },
                        refresh_token = new { type = SchemaString }
                    },
                    required = RequiredSessionTokens
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
                    type = SchemaObject,
                    properties = new
                    {
                        refresh_token = new { type = SchemaString }
                    },
                    required = RequiredRefreshToken
                }),
                CloneJson(new
                {
                    type = SchemaObject,
                    properties = new
                    {
                        success = new { type = SchemaBoolean }
                    },
                    required = RequiredSuccess
                }))
        ];
    }
}
