using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AgentChatWriteHoldTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly AgentCatalogService _catalogService = BuildCatalog();
    private readonly PendingAgentOperationStore _pendingOperationStore;
    private readonly AgentPolicyEvaluator _policyEvaluator;
    private readonly Guid _userId;

    public AgentChatWriteHoldTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"AgentChatWriteHoldTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        var user = User.Create("Alex", "alex@test.com").Value;
        user.GrantLifetimePro();
        _userId = user.Id;
        _dbContext.Users.Add(user);

        foreach (var key in _catalogService.GetCapabilities()
            .SelectMany(capability => capability.FeatureFlagKeys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create(key, true, null, key));
        }

        _dbContext.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        _pendingOperationStore = new PendingAgentOperationStore(_dbContext, settings);
        var stepUpBridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUpBridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        _policyEvaluator = new AgentPolicyEvaluator(
            _dbContext, _catalogService, _pendingOperationStore, stepUpBridge, settings);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static AgentCatalogService BuildCatalog() => new(ProductionTools());

    private static IReadOnlyList<IAiTool> ProductionTools()
    {
        return typeof(IAiTool).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(IAiTool).IsAssignableFrom(type))
            .Select(type => (IAiTool)RuntimeHelpers.GetUninitializedObject(type))
            .Select(tool => (IAiTool)new StubTool(tool.Name, tool.IsReadOnly))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static TheoryData<string> ChatWriteOperations()
    {
        var data = new TheoryData<string>();
        foreach (var operation in BuildCatalog().GetOperations()
            .Where(operation => operation.IsMutation && operation.IsAgentExecutable)
            .Where(operation => !AgentChatWriteHold.ExemptOperationIds.Contains(operation.Id))
            .OrderBy(operation => operation.Id, StringComparer.Ordinal))
        {
            data.Add(operation.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ChatWriteOperations))]
    public async Task ExecuteAsync_EveryChatWrite_WaitsForApproval(string operationId)
    {
        var executor = CreateExecutor(new AiToolRegistry([]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            operationId,
            Parse("""{"action":"unsupported_action"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, operationId);
        response.PendingOperation.Should().NotBeNull(operationId);
        response.PendingOperation!.ActionKey.Should().NotBeNullOrWhiteSpace(operationId);
    }

    [Fact]
    public async Task ExecuteAsync_ExemptOperation_RunsAtOnce()
    {
        var tool = new StubTool("suggest_breakdown");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "suggest_breakdown",
            Parse("""{"title":"Clean the flat"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(1);
        AgentChatWriteHold.ExemptOperationIds.Should().BeEquivalentTo(["suggest_breakdown"]);
    }

    [Fact]
    public async Task ExecuteAsync_LowRiskWriteOnMcpSurface_RunsAtOnce()
    {
        var tool = new StubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Beber agua","frequency_unit":"Day"}"""),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DeactivatedAccount_DeniesBeforeTheHold()
    {
        var user = _dbContext.Users.First(item => item.Id == _userId);
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        _dbContext.SaveChanges();
        var tool = new StubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Beber agua","frequency_unit":"Day"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        response.PolicyDenial!.Reason.Should().Be("account_deactivated");
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ToolThatAsksFirst_KeepsTheQuestionBeforeTheHold()
    {
        var tool = new ClarifyingStubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Rotina da manha"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.Operation.Payload.Should().BeOfType<NeedsClarificationPayload>();
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AnsweredClarification_WaitsForApproval()
    {
        var tool = new ClarifyingStubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Rotina da manha","frequency_unit":"Day"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        response.PendingOperation.Should().NotBeNull();
        tool.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ApprovedWrite_ConsumesTheConfirmation()
    {
        var tool = new StubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));
        var arguments = Parse("""{"title":"Beber agua","frequency_unit":"Day"}""");

        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, "create_habit", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        var confirmation = _pendingOperationStore.Confirm(_userId, held.PendingOperation!.Id);

        var executed = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, "create_habit", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation!.ConfirmationToken));

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        tool.Calls.Should().Be(1);
        _pendingOperationStore.GetExecution(_userId, held.PendingOperation.Id).Should().BeNull();
    }

    private AgentOperationExecutor CreateExecutor(AiToolRegistry toolRegistry)
    {
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>()).Returns((string?)null);
        var stepUpBridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUpBridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);

        return new AgentOperationExecutor(
            _catalogService,
            _policyEvaluator,
            Substitute.For<IAgentAuditService>(),
            ownership,
            stepUpBridge,
            toolRegistry,
            Substitute.For<IPendingOperationChangePreviewer>(),
            Substitute.For<IUnitOfWork>(),
            NullLogger<AgentOperationExecutor>.Instance);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private class StubTool(string name, bool isReadOnly = false) : IAiTool
    {
        public int Calls { get; private set; }

        public string Name => name;

        public string Description => name;

        public bool IsReadOnly => isReadOnly;

        public object GetParameterSchema() => new { type = "object" };

        public virtual Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolResult(true, EntityName: name));
        }
    }

    private sealed class ClarifyingStubTool(string name) : StubTool(name), IClarificationPrecheckTool
    {
        public bool NeedsClarification(JsonElement args) =>
            args.ValueKind == JsonValueKind.Object && !args.TryGetProperty("frequency_unit", out _);

        public override Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            base.ExecuteAsync(args, userId, ct);
            return Task.FromResult(new ToolResult(true, EntityName: name,
                Payload: new NeedsClarificationPayload("question", "frequency_unit", [])));
        }
    }
}
