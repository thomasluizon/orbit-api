using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task ContinuationBudget_InitialDenial_DoesNotDispatchTools()
    {
        SetupUserAndPayGate();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(Result.Failure<AiResponse>(AiBudgetExceededException.Error));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(UserId, "Put icons in all habits"), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("get_habits")]
    [InlineData("suggest_breakdown")]
    [InlineData("unknown_operation")]
    public async Task ContinuationBudget_NoAuthoritativeWriteOrPreview_RemainsExplicitFailure(string operation)
    {
        SetupUserAndPayGate();
        var tool = ReadTool(operation, new ToolResult(true));
        tool.IsReadOnly.Returns(operation == "get_habits");
        var handler = operation == "unknown_operation" ? CreateHandler() : CreateHandler(tool);
        SetupAiResponse(ToolResponse(operation, "no_effects", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(AiBudgetExceededException.Error));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Review my habits"), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        await _aiIntentService.ReceivedWithAnyArgs(1).ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ContinuationBudget_CertifiedCorrectionDenial_DoesNotOpenAnotherAttempt()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool, hold: true);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(AiBudgetExceededException.Error));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
        result.Value.Actions.Should().NotContain(item => item.Status == ActionStatus.Success);
        gateway.Requests.Should().ContainSingle();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _aiIntentService.ReceivedWithAnyArgs(1).ContinueWithToolResultsAsync(default!, default!, default, default);
    }
}
