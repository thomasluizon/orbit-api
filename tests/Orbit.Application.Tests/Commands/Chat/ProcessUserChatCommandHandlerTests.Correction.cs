using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Tests.Chat;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task RecoveryCapability_RejectedOccurrencePreparesOneCorrectedPreview()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool, hold: true);
        var original = WithMinuteSeconds(ProductionAdaptationArguments);
        (await tool.CheckArgumentsAsync(ParseArguments(original), UserId, default)).IsFailure.Should().BeTrue();
        SetupAiResponse(ToolResponse(tool.Name, "rejected_occurrence", original));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit at 21:00."), default);

        gateway.Requests.Should().HaveCount(2);
        (gateway.Requests[1] with { Arguments = gateway.Requests[0].Arguments }).Should().Be(gateway.Requests[0]);
        gateway.Responses[0].Should().BeOfType<RejectedBeforeDispatchResponse>();
        gateway.Responses[1].Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && JsonSerializer.SerializeToElement(results[0].Payload, (JsonSerializerOptions?)null)
                    .GetProperty("correction_allowed").GetBoolean()),
            Arg.Is<Func<AiStreamEvent, Task>?>(sink => sink == null), Arg.Any<CancellationToken>());
        gateway.Requests[1].Arguments.GetProperty("due_time").GetString().Should().Be("21:00");
        result.Value.Actions.Should().BeEmpty();
        result.Value.Operations.Should().BeEmpty();
        result.Value.PendingOperations.Should().ContainSingle();
        result.Value.PendingOperations![0].Items.Should().ContainSingle(item => item.EntityName.Contains("Adaptação", StringComparison.Ordinal));
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        _pendingOperationStore.Received(1).Revise(UserId, Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Is<string>(json => json.Contains("21:00", StringComparison.Ordinal)), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RecoveryCapability_TwoHistoricalHabitsAfterInjectedNoEffectRejectionExistExactlyOnce()
    {
        SetupUserAndPayGate();
        var rejected = false;
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), args =>
        {
            if (!rejected && args.GetProperty("title").GetString()!.Contains("Manutenção", StringComparison.Ordinal))
                return rejected = true;
            return false;
        });
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var saved = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new(tool.Name, "sibling", ParseArguments(ProductionAdaptationArguments)),
                new(tool.Name, "rejected", ParseArguments(ProductionMaintenanceRejectedArguments))
            ],
            ConversationContext = TestConversationContext
        });
        var correction = ParseArguments(ProductionMaintenanceRejectedArguments).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);
        correction["frequency_unit"] = JsonSerializer.SerializeToElement("Day");
        QueueCorrection(JsonSerializer.SerializeToElement(correction));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        gateway.Requests.Should().HaveCount(3);
        gateway.Responses.Select(response => response.Operation.Status).Should().Equal(
            AgentOperationStatus.Succeeded, AgentOperationStatus.Failed, AgentOperationStatus.Succeeded);
        gateway.Responses[1].Should().BeOfType<RejectedBeforeDispatchResponse>();
        saved.Should().HaveCount(2).And.OnlyHaveUniqueItems(habit => habit.Title);
        saved[1].Days.Should().BeEquivalentTo([DayOfWeek.Sunday, DayOfWeek.Wednesday]);
        saved[1].FrequencyUnit.Should().Be(FrequencyUnit.Day);
        saved[1].DueDate.Should().Be(new DateOnly(2026, 9, 15));
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Success);
        result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded);
        await _payGate.Received(1).TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(RecoveryMultiplexerCases))]
    public async Task RecoveryCapability_ConcreteMultiplexerIntentRejectsAnotherAction(Type toolType, string original, string candidate)
    {
        using var context = new HeldWriteTestContext();
        var notification = Notification.Create(UserId, "First", "Body");
        context.Records.Add(notification);
        original = original.Replace(RecoveryNotificationId, notification.Id.ToString(), StringComparison.Ordinal);
        var name = ((IAiTool)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(toolType)).Name;
        var real = (IPreparedIntentTool)context.Tool(name);
        await AssertCertifiedCandidateRefused(real, original, candidate);
        context.Commands.Should().BeEmpty();
        context.Added.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"action\":\"set_auto_sync\",\"enabled\":false}")]
    [InlineData("{\"retry_of\":\"rejected_occurrence\",\"action\":\"set_auto_sync\",\"enabled\":false}")]
    [InlineData("{\"action\":\"set_selected_calendars\",\"calendar_ids\":[]}")]
    public async Task RecoveryCapability_OppositeSetterOrForgedIdentityCannotConsumeFailure(string candidate)
    {
        using var context = new HeldWriteTestContext();
        await AssertCertifiedCandidateRefused((IPreparedIntentTool)context.Tool("manage_calendar_sync"),
            """{"action":"set_auto_sync","enabled":true}""", candidate);
        context.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoveryCapability_ChangedTargetCannotConsumeFailure()
    {
        using var context = new HeldWriteTestContext();
        var first = Notification.Create(UserId, "First", "Body");
        var second = Notification.Create(UserId, "Second", "Body");
        context.Records.AddRange([first, second]);
        await AssertCertifiedCandidateRefused((IPreparedIntentTool)context.Tool("update_notifications"),
            JsonSerializer.Serialize(new { action = "mark_read", notification_id = first.Id }),
            JsonSerializer.Serialize(new { action = "mark_read", notification_id = second.Id }));
        context.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoveryCapability_SubscribeCannotBecomeUnsubscribe()
    {
        using var context = new HeldWriteTestContext();
        await AssertCertifiedCandidateRefused((IPreparedIntentTool)context.Tool("update_notifications"),
            """{"action":"subscribe_push","endpoint":"device-token","p256dh":"fcm","auth":"auth"}""",
            """{"action":"unsubscribe_push","endpoint":"device-token"}""");
        context.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoveryCapability_UnrelatedSameNameDoesNotConsumeEitherOccurrence()
    {
        SetupUserAndPayGate();
        var remainingRejections = new List<JsonElement>
        {
            ParseArguments(ProductionAdaptationArguments),
            ParseArguments(ProductionMaintenanceRejectedArguments)
        };
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), args =>
        {
            var index = remainingRejections.FindIndex(original => JsonElement.DeepEquals(original, args));
            if (index < 0)
                return false;
            remainingRejections.RemoveAt(index);
            return true;
        });
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var saved = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var candidate = ParseArguments("""{"title":"Walk","frequency_unit":"Day"}""");
        var prepared = await tool.PrepareIntentAsync(candidate, UserId, default);
        prepared.Should().NotBeNull();
        prepared!.ArgumentValidation.IsSuccess.Should().BeTrue();
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(tool.Name, "first", ParseArguments(ProductionAdaptationArguments)),
                new(tool.Name, "second", ParseArguments(ProductionMaintenanceRejectedArguments))],
            ConversationContext = TestConversationContext
        });
        QueueCorrection(candidate);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        using (new AssertionScope())
        {
            remainingRejections.Should().BeEmpty();
            gateway.Responses[0].Should().BeOfType<RejectedBeforeDispatchResponse>();
            gateway.Responses[1].Should().BeOfType<RejectedBeforeDispatchResponse>();
            gateway.Requests.Should().HaveCount(2);
            saved.Should().BeEmpty();
            result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Failed);
            result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Failed);
            result.Value.PendingOperations.Should().BeNullOrEmpty();
            result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryCapability_SuccessfulSiblingCannotBeReplayedOrAttested(bool includeLegitimateCorrection)
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var saved = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(tool.Name, "sibling", ParseArguments(ProductionMaintenanceRejectedArguments)),
                new(tool.Name, "rejected", ParseArguments(WithMinuteSeconds(ProductionAdaptationArguments)))],
            ConversationContext = TestConversationContext
        });
        var candidates = new List<AiToolCall> { new(tool.Name, "replayed", ParseArguments(ProductionMaintenanceRejectedArguments)) };
        if (includeLegitimateCorrection)
            candidates.Add(new(tool.Name, "corrected", ParseArguments(ProductionAdaptationArguments)));
        QueueCorrections(candidates);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        using (new AssertionScope())
        {
            gateway.Responses[0].Should().BeOfType<PreparedToolOperationResponse>();
            gateway.Responses[0].Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
            gateway.Responses[1].Should().BeOfType<RejectedBeforeDispatchResponse>();
            gateway.Requests.Should().HaveCount(2);
            saved.Should().HaveCount(1);
            saved[0].Title.Should().Be(
                ParseArguments(ProductionMaintenanceRejectedArguments).GetProperty("title").GetString());
            result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Success);
            result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
            result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Succeeded);
            result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Failed);
            result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task RecoveryCapability_SecondRejectionStopsWithoutThirdAttempt(string language)
    {
        var user = User.Create("Alex", "alex@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var checks = 0;
        var tool = new CheckRejectionTool(RecoveryCreateHabitTool(), _ => ++checks is 1 or 3);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected_occurrence", ProductionAdaptationArguments));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().HaveCount(2);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.AiMessage.Should().Be(language == "en" ? EnglishToolFailureMessage : PortugueseToolFailureMessage);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoveryCapability_TextOnlyRecoveryCannotClaimSuccess()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected_occurrence", WithMinuteSeconds(ProductionAdaptationArguments)));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Everything was saved." }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().ContainSingle();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
    }

    [Fact]
    public async Task RecoveryCapability_PartialNotificationWriteHasNoCorrectionEvidence()
    {
        SetupUserAndPayGate();
        using var context = new HeldWriteTestContext();
        var first = Notification.Create(UserId, "First", "Body");
        var second = Notification.Create(UserId, "Second", "Body");
        context.Records.AddRange([first, second]);
        var owner = new DeleteNotificationCommandHandler(context.Repository<Notification>(), context.UnitOfWork);
        context.Mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var command = call.ArgAt<DeleteNotificationCommand>(0);
            return command.NotificationId == second.Id ? Result.Failure("synthetic_second_item_rejection")
                : await owner.Handle(command, call.ArgAt<CancellationToken>(1));
        });
        var tool = context.Tool("delete_notifications");
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var arguments = JsonSerializer.Serialize(new { action = "delete_selected", notification_ids = new[] { first.Id, second.Id } });
        SetupAiResponse(ToolResponse(tool.Name, "partial", arguments));
        QueueCorrections([new(tool.Name, "candidate", ParseArguments(arguments))]);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Delete these notifications."), default);

        first.IsDeleted.Should().BeTrue();
        second.IsDeleted.Should().BeFalse();
        await context.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        gateway.Requests.Should().ContainSingle();
        gateway.Responses[0].Should().BeOfType<PreparedToolOperationResponse>();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryCapability_ExceptionAfterDispatchRemainsUncertain(bool cancellation)
    {
        SetupUserAndPayGate();
        var writes = 0;
        var tool = new ExecutionOverrideTool(RecoveryCreateHabitTool(), () =>
        {
            writes++;
            return Task.FromException<ToolResult>(cancellation
                ? new OperationCanceledException("synthetic_dispatched_cancellation")
                : new InvalidOperationException("synthetic_post_write_failure"));
        });
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "uncertain", ProductionAdaptationArguments));
        QueueCorrection(ParseArguments(ProductionAdaptationArguments));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        writes.Should().Be(1);
        gateway.Requests.Should().ContainSingle();
        gateway.Responses[0].Should().BeOfType<AgentExecuteOperationResponse>();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryCapability_ExistingTokenCeilingPreventsCorrectionDispatch(bool ceilingReachedByCandidate)
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns([FeatureFlagKeys.AstraEvalTrace]);
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)) with
        {
            ReportedTokenCount = ceilingReachedByCandidate ? 1 : AppConstants.MaxAiRequestTotalTokens
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new(tool.Name, "candidate", ParseArguments(ProductionAdaptationArguments))],
                ConversationContext = TestConversationContext,
                ReportedTokenCount = AppConstants.MaxAiRequestTotalTokens
            }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit.",
            EvalTraceRequested: true), default);

        gateway.Requests.Should().ContainSingle();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        await _aiIntentService.Received(ceilingReachedByCandidate ? 1 : 0).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoveryCapability_LastAuthorizedRoundDoesNotOpenAnotherLoop()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var read = FakeTool("query_habits");
        read.IsReadOnly.Returns(true);
        read.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>()).Returns(new ToolResult(true));
        var handler = CreateHandler(tool, read);
        var gateway = InstallCorrectionGateway(tool, otherTools: [read]);
        SetupAiResponse(ToolResponse(read.Name, "read_0", "{}"));
        var continuations = Enumerable.Range(1, ChatTurnLimits.MaxToolIterations - 1)
            .Select(index => Result.Success(index == ChatTurnLimits.MaxToolIterations - 1
                ? ToolResponse(tool.Name, "last_rejected", WithMinuteSeconds(ProductionAdaptationArguments))
                : ToolResponse(read.Name, $"read_{index}", "{}"))).ToArray();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(continuations[0], continuations.Skip(1).ToArray());

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit after checking."), default);

        gateway.Requests.Should().HaveCount(ChatTurnLimits.MaxToolIterations);
        gateway.Responses.Last().Should().BeOfType<RejectedBeforeDispatchResponse>();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        await _aiIntentService.Received(ChatTurnLimits.MaxToolIterations - 1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoveryCapability_ExistingCancellationStopsBeforeCorrectionDispatch()
    {
        SetupUserAndPayGate();
        using var cancellation = new CancellationTokenSource();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var turnToken = call.ArgAt<CancellationToken>(3);
                turnToken.CanBeCanceled.Should().BeTrue();
                cancellation.Cancel();
                turnToken.IsCancellationRequested.Should().BeTrue();
                return Result.Success(ToolResponse(tool.Name, "candidate", ProductionAdaptationArguments));
            });

        var action = () => handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        gateway.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RecoveryCapability_AmbiguousPreparedParentageCannotConsumeEitherFailure()
    {
        SetupUserAndPayGate();
        using var context = new HeldWriteTestContext();
        var checks = 0;
        var tool = new CheckRejectionTool((IPreparedIntentTool)context.Tool("manage_calendar_sync"), _ => ++checks <= 2);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        const string original = """{"action":"set_auto_sync","enabled":true}""";
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(tool.Name, "first", ParseArguments(original)), new(tool.Name, "second",
                JsonSerializer.SerializeToElement(new { action = "set_auto_sync", enabled = true, suggestion_id = Guid.NewGuid() }))],
            ConversationContext = TestConversationContext
        });
        QueueCorrections([new(tool.Name, "candidate", ParseArguments(original))]);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Enable calendar sync."), default);

        gateway.Requests.Should().HaveCount(2);
        gateway.Responses.Should().OnlyContain(response => response is RejectedBeforeDispatchResponse);
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Failed);
        context.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoveryCapability_DuplicateCandidatesCannotSpendOneOccurrenceTwice()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        QueueCorrections([new(tool.Name, "first", ParseArguments(ProductionAdaptationArguments)),
            new(tool.Name, "second", ParseArguments(ProductionAdaptationArguments))]);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        gateway.Requests.Should().ContainSingle();
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Fact]
    public async Task RecoveryCapability_CandidateCannotSpendADeletionCodeAttempt()
    {
        using var context = new HeldWriteTestContext();
        await AssertCertifiedCandidateRefused((IPreparedIntentTool)context.Tool("manage_account"),
            """{"action":"request_deletion"}""", """{"action":"confirm_deletion","code":"wrong"}""");

        await context.Mediator.DidNotReceive().Send(Arg.Is<CheckChatCommandQuery>(query =>
            query.Command is Orbit.Application.Auth.Commands.ConfirmAccountDeletionCommand), Arg.Any<CancellationToken>());
        context.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryCapability_OnlyCandidateContinuationRunsAndItsFailureStaysTruthful(bool afterCorrection)
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool, hold: true);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        var failure = Result.Failure<AiResponse>(ErrorMessages.AiUnavailable);
        var first = afterCorrection ? Result.Success(ToolResponse(tool.Name, "candidate", ProductionAdaptationArguments)) : failure;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(first, failure);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        result.IsSuccess.Should().BeTrue();
        gateway.Requests.Should().HaveCount(afterCorrection ? 2 : 1);
        result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        if (afterCorrection)
        {
            result.Value.PendingOperations.Should().ContainSingle();
            result.Value.AiMessage.Should().BeNull();
        }
        else
            result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertCertifiedCandidateRefused(IPreparedIntentTool real, string original, string candidate)
    {
        SetupUserAndPayGate();
        var rejected = false;
        var tool = new CheckRejectionTool(real, _ => !rejected && (rejected = true));
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected_occurrence", original));
        QueueCorrections([new(tool.Name, "candidate", ParseArguments(candidate))]);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Complete the requested action."), default);

        gateway.Responses[0].Should().BeOfType<RejectedBeforeDispatchResponse>();
        gateway.Requests.Should().ContainSingle();
        result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Failed);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    private void QueueCorrection(JsonElement candidate) => QueueCorrections([new("create_habit", "candidate", candidate)]);

    private void QueueCorrections(IReadOnlyList<AiToolCall> candidates) =>
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = candidates, ConversationContext = TestConversationContext }),
                Result.Success(new AiResponse { TextMessage = "Prepared the requested changes." }));

    private static string WithMinuteSeconds(string json) => json.Replace("21:00", "21:00:00", StringComparison.Ordinal);

    private CorrectionGateway InstallCorrectionGateway(IAiTool tool, bool hold = false, params IAiTool[] otherTools)
    {
        _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<ToolResult>();
        var registry = new AiToolRegistry([tool, .. otherTools]);
        var catalog = new AgentCatalogService([tool, .. otherTools]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
        {
            var context = call.ArgAt<AgentPolicyEvaluationContext>(0);
            var capability = catalog.GetCapability(context.CapabilityId)!;
            var pending = hold ? new PendingAgentOperation(Guid.NewGuid(), capability.Id, capability.DisplayName,
                "Requested operation", capability.RiskClass, AgentConfirmationRequirement.FreshConfirmation,
                TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(10)) : null;
            return new AgentPolicyDecision(hold ? AgentPolicyDecisionStatus.ConfirmationRequired : AgentPolicyDecisionStatus.Allowed,
                capability, PendingOperation: pending);
        });
        _pendingOperationStore.Revise(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, registry);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, Substitute.For<IAgentStepUpAuthorizationBridge>(), registry,
            previewer, _pendingOperationStore, _unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
        var gateway = new CorrectionGateway(executor, [], []);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.ArgAt<AgentExecuteOperationRequest>(0);
            gateway.Requests.Add(request);
            var response = await executor.ExecuteAsync(request, call.ArgAt<CancellationToken>(1));
            gateway.Responses.Add(response);
            return response;
        });
        return gateway;
    }

    private sealed record CorrectionGateway(AgentOperationExecutor Executor,
        List<AgentExecuteOperationRequest> Requests, List<AgentExecuteOperationResponse> Responses);

    private sealed class ExecutionOverrideTool(IPreparedIntentTool inner, Func<Task<ToolResult>> execute) : IPreparedIntentTool
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public int Order => inner.Order;
        public object GetParameterSchema() => inner.GetParameterSchema();
        public Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct) =>
            inner.PrepareIntentAsync(args, userId, ct);
        public Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) => inner.CheckArgumentsAsync(args, userId, ct);
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => execute();
    }

    private sealed class CheckRejectionTool(IPreparedIntentTool inner, Func<JsonElement, bool> reject) : IPreparedIntentTool
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public int Order => inner.Order;
        public object GetParameterSchema() => inner.GetParameterSchema();
        public async Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            var prepared = await inner.PrepareIntentAsync(args, userId, ct);
            return prepared is not null && reject(args)
                ? prepared with { ArgumentValidation = Result.Failure("synthetic_no_effect_rejection") } : prepared;
        }
        public Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
            inner.CheckArgumentsAsync(args, userId, ct);
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => inner.ExecuteAsync(args, userId, ct);
    }
}
