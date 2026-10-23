using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task RecoverySurvivor_OnlyRejectedSiblingsReceiveAnOffer()
    {
        SetupUserAndPayGate();
        var checks = 0;
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), _ => ++checks == 2);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupRecoverySiblings(tool);
        IReadOnlyList<AiToolCallResult>? offered = null;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                offered = call.ArgAt<IReadOnlyList<AiToolCallResult>>(1).ToArray();
                return Result.Success(ToolResponse(tool.Name, "candidate", ProductionMaintenanceRejectedArguments));
            });

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        gateway.Requests.Should().HaveCount(3);
        offered.Should().HaveCount(2);
        offered![0].Success.Should().BeTrue();
        var successfulPayload = JsonSerializer.SerializeToElement(offered[0].Payload);
        (successfulPayload.ValueKind == JsonValueKind.Object
            && successfulPayload.TryGetProperty("correction_allowed", out _)).Should().BeFalse();
        offered[1].Success.Should().BeFalse();
        JsonSerializer.SerializeToElement(offered[1].Payload).GetProperty("correction_allowed").GetBoolean().Should().BeTrue();
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Success);
    }

    [Fact]
    public async Task RecoverySurvivor_UncertainSiblingClosesTheCorrectionRound()
    {
        SetupUserAndPayGate();
        var real = RecoveryCreateHabitTool();
        var executed = new ExecutionOverrideTool(real, async () =>
        {
            await real.ExecuteAsync(ParseArguments(ProductionAdaptationArguments), UserId, default);
            return new ToolResult(false, Error: "synthetic_failure_after_write");
        });
        var checks = 0;
        var tool = new CheckRejectionTool(executed, _ => ++checks == 2);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupRecoverySiblings(tool);
        QueueCorrection(ParseArguments(ProductionMaintenanceRejectedArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        gateway.Requests.Should().HaveCount(2);
        gateway.Responses[0].Should().BeOfType<PreparedToolOperationResponse>();
        gateway.Responses[1].Should().BeOfType<RejectedBeforeDispatchResponse>();
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Fact]
    public async Task RecoverySurvivor_CandidateWithRejectedValidationCannotDispatch()
    {
        SetupUserAndPayGate();
        var checks = 0;
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), _ => ++checks == 2);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().ContainSingle();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        checks.Should().Be(2);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Fact]
    public async Task RecoverySurvivor_CancellationDuringPreparationStopsDispatch()
    {
        SetupUserAndPayGate();
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), _ =>
        {
            if (++checks == 2)
                cancellation.Cancel();
            return false;
        });
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var action = () => handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        checks.Should().Be(2);
        gateway.Requests.Should().ContainSingle();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverySurvivor_CancellationAfterRejectionPreventsContinuation()
    {
        SetupUserAndPayGate();
        using var cancellation = new CancellationTokenSource();
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), _ =>
        {
            cancellation.Cancel();
            return false;
        });
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var action = () => handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        gateway.Requests.Should().ContainSingle();
        gateway.Responses[0].Should().BeOfType<RejectedBeforeDispatchResponse>();
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RecoverySurvivor_TwoRejectedOccurrencesEachRecoverOnce()
    {
        SetupUserAndPayGate();
        var checks = 0;
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), _ => ++checks <= 2);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupRecoverySiblings(tool);
        QueueRecoverySiblings(tool);
        var saved = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        gateway.Requests.Should().HaveCount(4);
        gateway.Responses.Take(2).Should().OnlyContain(response => response is RejectedBeforeDispatchResponse);
        saved.Select(habit => habit.Title).Should().BeEquivalentTo(
            ParseArguments(ProductionAdaptationArguments).GetProperty("title").GetString(),
            ParseArguments(ProductionMaintenanceRejectedArguments).GetProperty("title").GetString());
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Success);
        result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverySurvivor_FirstCorrectionFailureStopsRemainingSiblings()
    {
        SetupUserAndPayGate();
        var checks = 0;
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), _ => ++checks <= 2 || checks == 5);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupRecoverySiblings(tool);
        QueueRecoverySiblings(tool);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        gateway.Requests.Should().HaveCount(3);
        gateway.Responses.Should().OnlyContain(response => response is RejectedBeforeDispatchResponse);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Failed);
        result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Fact]
    public async Task RecoverySurvivor_ExactCumulativeTokenCeilingStillAllowsBoundCorrection()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)) with { ReportedTokenCount = 1 });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse(tool.Name, "candidate", ProductionAdaptationArguments) with
            {
                ReportedTokenCount = AppConstants.MaxAiRequestTotalTokens - 1
            }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().HaveCount(2);
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Success);
    }

    [Fact]
    public async Task RecoverySurvivor_PolicyDenialAfterCorrectionIsReported()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var capability = new AgentCatalogService([tool]).GetCapabilityByChatTool(tool.Name)!;
        gateway.Policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(
            new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "synthetic_correction_policy_denial"));
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().HaveCount(2);
        result.Value.PolicyDenials.Should().ContainSingle();
        result.Value.PolicyDenials![0].Should().NotBeNull();
        result.Value.PolicyDenials![0].Reason.Should().Be("synthetic_correction_policy_denial");
        result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Denied);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverySurvivor_FailedCorrectionCannotAdvertiseRelatedSurfaces()
    {
        SetupUserAndPayGate();
        var tool = new ExecutionOverrideTool(RecoveryCreateHabitTool(), () => Task.FromResult(
            new ToolResult(false, Error: "synthetic_correction_failure", Payload: new { related_surfaces = new[] { "today" } })));
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().HaveCount(2);
        result.Value.RelatedSurfaces.Should().BeNullOrEmpty();
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Fact]
    public async Task RecoverySurvivor_CandidateCannotDropBlankListEntries()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var original = RecoveryArgumentsWith(WithMinuteSeconds(ProductionAdaptationArguments), "tag_names", new[] { "Study" });
        var candidate = RecoveryArgumentsWith(ProductionAdaptationArguments, "tag_names", new[] { "Study", " " });
        SetupAiResponse(ToolResponse(tool.Name, "rejected", original.GetRawText()));
        QueueCorrection(candidate);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit tagged Study."), default);

        gateway.Responses[0].Should().BeOfType<RejectedBeforeDispatchResponse>();
        gateway.Requests.Should().ContainSingle();
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverySurvivor_CorrectionRetainsStepUpRequirement()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool, hold: true);
        gateway.Bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConfirmationRequirement.StepUp);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().HaveCount(2);
        gateway.Responses[1].Operation.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.StepUp);
        gateway.Policy.Received(1).Evaluate(Arg.Is<AgentPolicyEvaluationContext>(context =>
            context.ConfirmationRequirementOverride == AgentConfirmationRequirement.StepUp));
        result.Value.Actions.Should().BeEmpty();
        result.Value.PendingOperations.Should().ContainSingle();
    }

    [Fact]
    public async Task RecoverySurvivor_UnpersistedPreviewIsReportedAsFailure()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool, hold: true);
        _pendingOperationStore.Revise(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().HaveCount(2);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverySurvivor_EmptyJwtScopesKeepTheOriginalAuthority()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var capability = new AgentCatalogService([tool]).GetCapabilityByChatTool(tool.Name)!;
        gateway.Policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
            new AgentPolicyDecision(call.ArgAt<AgentPolicyEvaluationContext>(0).GrantedScopes.Contains(capability.Scope)
                ? AgentPolicyDecisionStatus.Allowed : AgentPolicyDecisionStatus.Denied, capability, "synthetic_scope_check"));
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit.", GrantedScopes: []), default);

        gateway.Requests.Should().HaveCount(2);
        gateway.Requests.Should().OnlyContain(request => request.AuthMethod == AgentAuthMethod.Jwt && request.GrantedScopes!.Count == 0);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Success);
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverySurvivor_ReadFailureCannotConsumeTheWriteCorrection()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var read = FakeTool("query_habits");
        read.IsReadOnly.Returns(true);
        read.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(false, Error: "synthetic_read_failure"));
        var handler = CreateHandler(tool, read);
        var gateway = InstallCorrectionGateway(tool, otherTools: [read]);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(read.Name, "read", ParseArguments("{}")),
                new(tool.Name, "rejected", ParseArguments(WithMinuteSeconds(ProductionAdaptationArguments)))],
            ConversationContext = TestConversationContext
        });
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Check and create the adaptation habit."), default);

        gateway.Requests.Should().HaveCount(3);
        gateway.Responses[0].Operation.Status.Should().Be(AgentOperationStatus.Failed);
        gateway.Responses[1].Should().BeOfType<RejectedBeforeDispatchResponse>();
        gateway.Responses[2].Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Success);
        result.Value.Operations.Should().HaveCount(2);
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    private void SetupRecoverySiblings(IAiTool tool) => SetupAiResponse(new AiResponse
    {
        ToolCalls = [new(tool.Name, "adaptation", ParseArguments(ProductionAdaptationArguments)),
            new(tool.Name, "maintenance", ParseArguments(ProductionMaintenanceRejectedArguments))],
        ConversationContext = TestConversationContext
    });

    private void QueueRecoverySiblings(IAiTool tool) => QueueCorrections(
        [new(tool.Name, "corrected_adaptation", ParseArguments(ProductionAdaptationArguments)),
            new(tool.Name, "corrected_maintenance", ParseArguments(ProductionMaintenanceRejectedArguments))]);

    private static JsonElement RecoveryArgumentsWith<T>(string arguments, string field, T value)
    {
        var fields = ParseArguments(arguments).EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        fields[field] = JsonSerializer.SerializeToElement(value);
        return JsonSerializer.SerializeToElement(fields);
    }
}
