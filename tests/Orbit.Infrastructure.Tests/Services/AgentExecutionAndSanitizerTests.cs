using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Prompts;

namespace Orbit.Infrastructure.Tests.Services;

public class AgentExecutionAndSanitizerTests
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void PromptDataSanitizer_SanitizeInline_CollapsesWhitespace()
    {
        var result = PromptDataSanitizer.SanitizeInline("  hello\t\r\nworld  ");

        result.Should().Be("hello world");
    }

    [Fact]
    public void PromptDataSanitizer_SanitizeBlock_PreservesSingleNewlines()
    {
        var result = PromptDataSanitizer.SanitizeBlock("first\r\n\r\nsecond\tthird");

        result.Should().Be("first\nsecond third");
    }

    [Fact]
    public void PromptDataSanitizer_QuoteInline_EscapesQuotesAndSlashes()
    {
        var result = PromptDataSanitizer.QuoteInline("say \"hi\" \\ now");

        result.Should().Be("\"say \\\"hi\\\" \\\\ now\"");
    }

    [Fact]
    public void PromptDataSanitizer_SanitizeInline_TruncatesLongValues()
    {
        var result = PromptDataSanitizer.SanitizeInline("abcdefghijklmnopqrstuvwxyz", 10);

        result.Should().Be("abcdefg...");
    }

    [Fact]
    public async Task AgentOperationExecutor_ReturnsUnsupportedWhenOperationIsMissing()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        catalog.GetOperation("missing").Returns((AgentOperation?)null);
        var executor = CreateExecutor(catalog);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            "missing",
            Parse("""{"id":"1"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.UnsupportedByPolicy);
        response.PolicyDenial!.Reason.Should().Be("unsupported_by_policy");
    }

    [Fact]
    public async Task AgentOperationExecutor_DeniesDirectUserFlowOperations()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.AuthManage, AgentScopes.ManageAuth, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("send_auth_code", capability.Id, isMutation: true, isAgentExecutable: false, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var auditService = Substitute.For<IAgentAuditService>();
        var executor = CreateExecutor(catalog, auditService: auditService);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            default,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        response.PolicyDenial!.Reason.Should().Be("direct_user_flow_required");
        await auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry =>
                entry.CapabilityId == capability.Id &&
                entry.OutcomeStatus == AgentOperationStatus.Denied &&
                entry.Error == "direct_user_flow_required"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentOperationExecutor_DeniesWhenTargetIsNotOwned()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsDelete, AgentScopes.DeleteHabits, AgentRiskClass.Destructive, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("delete_habit", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Destructive);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(operation.Id, UserId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns("target_not_owned:delete_habit:habit");
        var executor = CreateExecutor(catalog, ownershipService: ownership);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"habit_id":"123"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        response.PolicyDenial!.Reason.Should().Be("target_not_owned:delete_habit:habit");
    }

    [Fact]
    public async Task AgentOperationExecutor_PropagatesPolicyDenial()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsWrite, AgentScopes.WriteHabits, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("create_habit", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "missing_scope:write_habits"));
        var executor = CreateExecutor(catalog, policyEvaluator: policy);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"title":"Test"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.ApiKey,
            ["read_habits"]));

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        response.PolicyDenial!.Reason.Should().Be("missing_scope:write_habits");
    }

    [Fact]
    public async Task AgentOperationExecutor_ReturnsPendingConfirmation()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsDelete, AgentScopes.DeleteHabits, AgentRiskClass.Destructive, AgentConfirmationRequirement.FreshConfirmation, isMutation: true);
        var operation = CreateOperation("delete_habit", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.FreshConfirmation, AgentRiskClass.Destructive);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var pending = new PendingAgentOperation(Guid.NewGuid(), capability.Id, capability.DisplayName, "Delete habit", capability.RiskClass, capability.ConfirmationRequirement, DateTime.UtcNow.AddMinutes(5));
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.ConfirmationRequired, capability, "confirmation_required", pending));
        var executor = CreateExecutor(catalog, policyEvaluator: policy);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"habit_id":"123"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        response.PendingOperation.Should().Be(pending);
    }

    [Fact]
    public async Task AgentOperationExecutor_HashesFingerprintSoLargePayloadsFitTheColumn()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsWrite, AgentScopes.WriteHabits, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("bulk_create_habits", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        var capturedContexts = new List<AgentPolicyEvaluationContext>();
        policy.Evaluate(Arg.Do<AgentPolicyEvaluationContext>(capturedContexts.Add))
            .Returns(callInfo => new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied,
                capability,
                "missing_scope:write_habits"));
        var executor = CreateExecutor(catalog, policyEvaluator: policy);

        var habits = string.Join(",", Enumerable.Range(1, 10).Select(index =>
            $$"""{"title":"Generic unique task number {{index}} with a deliberately long descriptive title"}"""));
        var largeArguments = Parse($$"""{"habits":[{{habits}}]}""");

        await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId, operation.Id, largeArguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId, operation.Id, largeArguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId, operation.Id, Parse("""{"habits":[{"title":"Single"}]}"""), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        capturedContexts.Should().HaveCount(3);
        capturedContexts[0].OperationFingerprint.Should().HaveLength(64);
        capturedContexts[0].OperationFingerprint.Should().MatchRegex("^[0-9A-F]{64}$");
        capturedContexts[1].OperationFingerprint.Should().Be(capturedContexts[0].OperationFingerprint);
        capturedContexts[2].OperationFingerprint.Should().NotBe(capturedContexts[0].OperationFingerprint);
    }

    [Fact]
    public async Task AgentOperationExecutor_UsesFallbackScopesForJwtRequests()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsWrite, AgentScopes.WriteHabits, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("create_habit", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        catalog.GetCapabilities().Returns([capability]);
        var capturedContext = default(AgentPolicyEvaluationContext);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Do<AgentPolicyEvaluationContext>(context => capturedContext = context))
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability));
        var executor = CreateExecutor(
            catalog,
            policyEvaluator: policy,
            toolRegistry: new AiToolRegistry([new StubTool(operation.Id, (_, _, _) => Task.FromResult(new ToolResult(true, EntityId: "1", EntityName: "Habit")))]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"title":"Test"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        capturedContext.Should().NotBeNull();
        capturedContext.GrantedScopes.Should().Contain(AgentScopes.WriteHabits);
    }

    [Fact]
    public async Task AgentOperationExecutor_WritesAuditRowForSuccessfulReadOnlyTool()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsRead, AgentScopes.ReadHabits, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: false);
        var operation = CreateOperation("query_habits", capability.Id, isMutation: false, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability));
        var auditService = Substitute.For<IAgentAuditService>();
        var executor = CreateExecutor(
            catalog,
            policyEvaluator: policy,
            auditService: auditService,
            toolRegistry: new AiToolRegistry([new StubTool(
                operation.Id,
                (_, _, _) => Task.FromResult(new ToolResult(true, EntityName: "Found 3 habits")),
                isReadOnly: true)]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"scope":"today"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry =>
                entry.CapabilityId == capability.Id &&
                entry.SourceName == operation.Id &&
                entry.OutcomeStatus == AgentOperationStatus.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentOperationExecutor_ReturnsToolFailure()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsWrite, AgentScopes.WriteHabits, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("create_habit", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability));
        var executor = CreateExecutor(
            catalog,
            policyEvaluator: policy,
            toolRegistry: new AiToolRegistry([new StubTool(operation.Id, (_, _, _) => Task.FromResult(new ToolResult(false, EntityId: UserId.ToString(), Error: "tool_failed")))]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"title":"Test"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        response.Operation.PolicyReason.Should().Be("tool_failed");
        response.PolicyDenial.Should().BeNull();
    }

    [Fact]
    public async Task AgentOperationExecutor_MapsPayGateToolFailuresToDeniedWithPolicyDenial()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsWrite, AgentScopes.WriteHabits, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("create_sub_habit", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability));
        var executor = CreateExecutor(
            catalog,
            policyEvaluator: policy,
            toolRegistry: new AiToolRegistry([new StubTool(operation.Id, (_, _, _) => Task.FromResult(
                ToolResult.FromFailure(Result.PayGateFailure("Sub-habits are a Pro feature. Upgrade to unlock!"))))]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"parent_habit_id":"123","title":"Test"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        response.Operation.PolicyReason.Should().Be("Sub-habits are a Pro feature. Upgrade to unlock!");
        response.PolicyDenial.Should().NotBeNull();
        response.PolicyDenial!.Reason.Should().Be("Sub-habits are a Pro feature. Upgrade to unlock!");
    }

    [Fact]
    public async Task AgentOperationExecutor_MapsUnexpectedExceptions()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsWrite, AgentScopes.WriteHabits, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("create_habit", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability));
        var executor = CreateExecutor(
            catalog,
            policyEvaluator: policy,
            toolRegistry: new AiToolRegistry([new StubTool(operation.Id, (_, _, _) => throw new InvalidOperationException("boom"))]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"title":"Test"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        response.Operation.PolicyReason.Should().Be("unexpected_error");
    }

    [Fact]
    public async Task AgentOperationExecutor_RetriesConcurrencyConflictForRetryableTool()
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.GoalsWrite, AgentScopes.WriteGoals, AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: true);
        var operation = CreateOperation("update_goal", capability.Id, isMutation: true, isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability));
        var unitOfWork = Substitute.For<IUnitOfWork>();

        var attempts = 0;
        var tool = new RetryableStubTool(operation.Id, (_, _, _) =>
        {
            attempts++;
            return attempts == 1
                ? throw new DbUpdateConcurrencyException("simulated stale goal")
                : Task.FromResult(new ToolResult(true, EntityId: "1", EntityName: "Goal"));
        });
        var executor = CreateExecutor(
            catalog, policyEvaluator: policy, toolRegistry: new AiToolRegistry([tool]), unitOfWork: unitOfWork);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId,
            operation.Id,
            Parse("""{"goal_id":"1"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        attempts.Should().Be(2);
        unitOfWork.Received(1).ResetTracking();
    }

    private static AgentOperationExecutor CreateExecutor(
        IAgentCatalogService catalogService,
        IAgentPolicyEvaluator? policyEvaluator = null,
        IAgentAuditService? auditService = null,
        IAgentTargetOwnershipService? ownershipService = null,
        AiToolRegistry? toolRegistry = null,
        IUnitOfWork? unitOfWork = null)
    {
        if (ownershipService is null)
        {
            ownershipService = Substitute.For<IAgentTargetOwnershipService>();
            ownershipService.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
        }

        return new AgentOperationExecutor(
            catalogService,
            policyEvaluator ?? Substitute.For<IAgentPolicyEvaluator>(),
            auditService ?? Substitute.For<IAgentAuditService>(),
            ownershipService,
            toolRegistry ?? new AiToolRegistry([]),
            unitOfWork ?? Substitute.For<IUnitOfWork>(),
            NullLogger<AgentOperationExecutor>.Instance);
    }

    private static AgentCapability CreateCapability(
        string id,
        string scope,
        AgentRiskClass riskClass,
        AgentConfirmationRequirement confirmationRequirement,
        bool isMutation)
    {
        return new AgentCapability(
            id,
            id,
            id,
            "test",
            scope,
            riskClass,
            isMutation,
            false,
            confirmationRequirement);
    }

    private static AgentOperation CreateOperation(
        string id,
        string capabilityId,
        bool isMutation,
        bool isAgentExecutable,
        AgentConfirmationRequirement confirmationRequirement,
        AgentRiskClass riskClass)
    {
        return new AgentOperation(
            id,
            id,
            id,
            capabilityId,
            riskClass,
            confirmationRequirement,
            isMutation,
            isAgentExecutable,
            EmptySchema,
            EmptySchema);
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed class StubTool(string name, Func<JsonElement, Guid, CancellationToken, Task<ToolResult>> executeAsync, bool isReadOnly = false) : IAiTool
    {
        public string Name => name;
        public string Description => name;
        public bool IsReadOnly => isReadOnly;
        public object GetParameterSchema() => new { };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => executeAsync(args, userId, ct);
    }

    private sealed class RetryableStubTool(string name, Func<JsonElement, Guid, CancellationToken, Task<ToolResult>> executeAsync)
        : IAiTool, IConcurrencyRetryableTool
    {
        public string Name => name;
        public string Description => name;
        public bool IsReadOnly => false;
        public object GetParameterSchema() => new { };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => executeAsync(args, userId, ct);
    }
}
