using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Extensions;

/// <summary>
/// The confirmation gate on the MCP surface, driven through the real middleware entry point
/// (<see cref="WebApplicationExtensions.HandleMcpToolCallAsync"/>) into the real
/// <see cref="McpExecutorBridge"/>, <see cref="AgentOperationExecutor"/> and
/// <see cref="AgentPolicyEvaluator"/>. The middleware holds no confirmation token, so a second
/// policy evaluation there can only refuse a step-up capability forever: the token a client obtains
/// is single use and is bound to the executor's operation fingerprint, not to the middleware's.
/// These tests pin that the middleware defers the gate to the executor, that the refusal names the
/// tools a client must call, and that a confirmed retry runs the operation.
/// </summary>
public class McpConfirmationGateTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly AgentCatalogService _catalogService;
    private readonly PendingAgentOperationStore _pendingOperationStore;
    private readonly AgentPolicyEvaluator _policyEvaluator;
    private readonly AgentStepUpService _stepUpService;
    private readonly IAgentAuditService _auditService = Substitute.For<IAgentAuditService>();
    private readonly ApiKeyTools _apiKeyTools;
    private readonly AgentTools _agentTools;
    private readonly ClaimsPrincipal _user;
    private readonly ClaimsPrincipal _apiKeyUser;
    private readonly Guid _userId;
    private readonly Guid _keyId = Guid.NewGuid();
    private string? _emailedCode;

    public McpConfirmationGateTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"McpConfirmationGateTests_{Guid.NewGuid()}")
            .Options;
        _dbContext = new OrbitDbContext(options);

        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);
        _userId = user.Id;
        _dbContext.Users.Add(user);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("api_keys", true, "Pro", "API keys"));
        _dbContext.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        var manageApiKeysTool = new StubManageApiKeysTool(_keyId);
        _catalogService = new AgentCatalogService([manageApiKeysTool]);
        _pendingOperationStore = new PendingAgentOperationStore(_dbContext, settings);
        _policyEvaluator = new AgentPolicyEvaluator(_dbContext, _catalogService, _pendingOperationStore, settings);

        var emailService = Substitute.For<IEmailService>();
        emailService.SendVerificationCodeAsync(
                Arg.Any<string>(),
                Arg.Do<string>(code => _emailedCode = code),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _stepUpService = new AgentStepUpService(_dbContext, emailService, settings);

        var ownershipService = Substitute.For<IAgentTargetOwnershipService>();
        ownershipService.GetDenialReasonAsync(
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<JsonElement>(),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var executor = new AgentOperationExecutor(
            _catalogService,
            _policyEvaluator,
            _auditService,
            ownershipService,
            new AiToolRegistry([manageApiKeysTool]),
            Substitute.For<IUnitOfWork>(),
            NullLogger<AgentOperationExecutor>.Instance);

        _apiKeyTools = new ApiKeyTools(Substitute.For<IMediator>(), new McpExecutorBridge(executor));
        _agentTools = new AgentTools(_catalogService, executor, _pendingOperationStore, _stepUpService);
        _user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())],
            "JwtBearer"));
        _apiKeyUser = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim("auth_method", "api_key")
            ],
            "ApiKey"));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StepUpTool_WithoutToken_IsRefusedByTheExecutorAndNamesTheRecoveryPath()
    {
        var outcome = await InvokeManageApiKeysAsync(confirmationToken: null);

        outcome.PolicyError.Should().BeNull();
        outcome.ToolOutput.Should().Contain("Step-up verification required");
        outcome.ToolOutput.Should().Contain("step_up_agent_operation_v2");
        outcome.ToolOutput.Should().Contain("verify_step_up_agent_operation_v2");
        outcome.ToolOutput.Should().Contain("confirm_agent_operation_v2");

        var pendingOperation = _dbContext.PendingAgentOperations.Single();
        pendingOperation.CapabilityId.Should().Be(AgentCapabilityIds.ApiKeysManage);
        pendingOperation.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.StepUp);
    }

    [Fact]
    public async Task StepUpTool_AfterTheFourRecoveryToolsRun_RunsInsteadOfBeingRefused()
    {
        var refusal = await InvokeManageApiKeysAsync(confirmationToken: null);
        refusal.ToolOutput.Should().Contain("Step-up verification required");

        var pendingOperationId = _dbContext.PendingAgentOperations.Single().Id.ToString();

        var challenge = await _agentTools.StepUpAgentOperation(
            _user, pendingOperationId, "en", CancellationToken.None);
        _emailedCode.Should().NotBeNull();

        var verified = await _agentTools.VerifyStepUpAgentOperation(
            _user, pendingOperationId, challenge.ChallengeId.ToString(), _emailedCode!, CancellationToken.None);
        verified.Id.ToString().Should().Be(pendingOperationId);
        _dbContext.PendingAgentOperations.Single().StepUpSatisfiedAtUtc.Should().NotBeNull();

        var confirmation = _agentTools.ConfirmAgentOperation(_user, pendingOperationId);
        confirmation.Should().NotBeNull();

        var outcome = await InvokeManageApiKeysAsync(confirmation!.ConfirmationToken);

        outcome.PolicyError.Should().BeNull();
        outcome.ToolOutput.Should().Be($"Revoked API key {_keyId}.");
    }

    [Fact]
    public async Task TheRecoveryTools_RefuseAnApiKeyCredentialSoThatDoorStaysClosed()
    {
        await InvokeManageApiKeysAsync(confirmationToken: null);
        var pendingOperationId = _dbContext.PendingAgentOperations.Single().Id.ToString();

        var stepUp = () => _agentTools.StepUpAgentOperation(
            _apiKeyUser, pendingOperationId, "en", CancellationToken.None);
        var verify = () => _agentTools.VerifyStepUpAgentOperation(
            _apiKeyUser, pendingOperationId, Guid.NewGuid().ToString(), "123456", CancellationToken.None);
        var confirm = () => _agentTools.ConfirmAgentOperation(_apiKeyUser, pendingOperationId);

        await stepUp.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("API key credentials cannot satisfy step-up authorization.");
        await verify.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("API key credentials cannot satisfy step-up authorization.");
        confirm.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("API key credentials cannot confirm pending operations.");

        _dbContext.PendingAgentOperations.Single().ConfirmedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task StepUpTool_WithAnUnknownToken_StaysRefused()
    {
        await InvokeManageApiKeysAsync(confirmationToken: null);

        var outcome = await InvokeManageApiKeysAsync("agc_not-a-real-token");

        outcome.PolicyError.Should().BeNull();
        outcome.ToolOutput.Should().Contain("Step-up verification required");
    }

    [Fact]
    public async Task ToolWithoutConfirmationRequirement_IsStillGatedByTheMiddleware()
    {
        var context = CreateHttpContext(_apiKeyUser);
        var toolRan = false;

        using var document = CreateToolCallDocument(
            "list_habits",
            new RequestId(7),
            new Dictionary<string, JsonElement>());
        var body = document.RootElement.GetRawText();
        WebApplicationExtensions.TryGetMcpToolCall(
            document.RootElement, out var toolName, out var requestId, out var operationId, out var fingerprint)
            .Should().BeTrue();

        await WebApplicationExtensions.HandleMcpToolCallAsync(
            context,
            () => { toolRan = true; return Task.CompletedTask; },
            body,
            new WebApplicationExtensions.McpToolCallRequest(toolName!, requestId, operationId, fingerprint));

        toolRan.Should().BeFalse();
        ReadPolicyError(context).Should().Be($"missing_scope:{AgentScopes.ReadHabits}");
    }

    private async Task<McpCallOutcome> InvokeManageApiKeysAsync(string? confirmationToken)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["action"] = JsonSerializer.SerializeToElement("revoke"),
            ["keyId"] = JsonSerializer.SerializeToElement(_keyId.ToString())
        };

        if (confirmationToken is not null)
            arguments["confirmationToken"] = JsonSerializer.SerializeToElement(confirmationToken);

        using var document = CreateToolCallDocument("manage_api_keys", new RequestId(1), arguments);
        var body = document.RootElement.GetRawText();
        WebApplicationExtensions.TryGetMcpToolCall(
            document.RootElement, out var toolName, out var requestId, out var operationId, out var fingerprint)
            .Should().BeTrue();

        var context = CreateHttpContext(_user);
        string? toolOutput = null;

        await WebApplicationExtensions.HandleMcpToolCallAsync(
            context,
            async () => toolOutput = await _apiKeyTools.ManageApiKeys(
                _user,
                "revoke",
                keyId: _keyId.ToString(),
                confirmationToken: confirmationToken),
            body,
            new WebApplicationExtensions.McpToolCallRequest(toolName!, requestId, operationId, fingerprint));

        return new McpCallOutcome(toolOutput, ReadPolicyError(context));
    }

    private DefaultHttpContext CreateHttpContext(ClaimsPrincipal user)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAgentCatalogService>(_catalogService)
            .AddSingleton<IAgentPolicyEvaluator>(_policyEvaluator)
            .AddSingleton(_auditService)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = services,
            User = user,
            Response = { Body = new MemoryStream() }
        };
    }

    private static string? ReadPolicyError(HttpContext context)
    {
        if (context.Response.Body.Length == 0)
            return null;

        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.TryGetProperty("error", out var error)
            ? error.GetProperty("message").GetString()
            : null;
    }

    private static JsonDocument CreateToolCallDocument(
        string toolName,
        RequestId requestId,
        IDictionary<string, JsonElement> arguments)
    {
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(
                new CallToolRequestParams { Name = toolName, Arguments = arguments },
                McpJsonUtilities.DefaultOptions)
        };

        return JsonDocument.Parse(
            JsonSerializer.Serialize<JsonRpcMessage>(request, McpJsonUtilities.DefaultOptions));
    }

    private sealed record McpCallOutcome(string? ToolOutput, string? PolicyError);

    private sealed class StubManageApiKeysTool(Guid keyId) : IAiTool
    {
        public string Name => "manage_api_keys";

        public string Description => "Creates and revokes scoped API keys.";

        public object GetParameterSchema() => new { type = "object" };

        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
            => Task.FromResult(new ToolResult(true, keyId.ToString(), "CI key"));
    }
}
