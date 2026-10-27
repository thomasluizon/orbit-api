using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EvalUnmetered_OnlyResolvedFlagAllowsToolsPastRequestTokenCeiling(bool exempt, bool initialCeiling)
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(exempt ? [FeatureFlagKeys.AstraEvalUnmetered] : []);
        var tool = ReadTool("query_habits", new ToolResult(true));
        var handler = CreateHandler(tool);
        var response = ToolResponse(tool.Name, "read_1", "{}") with
        {
            ReportedTokenCount = initialCeiling ? AppConstants.MaxAiRequestTotalTokens + 1 : 60_000
        };
        SetupAiResponse(response);
        var continuation = ToolResponse(tool.Name, "read_2", "{}") with { ReportedTokenCount = 60_001 };
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(continuation),
                Result.Success(new AiResponse { TextMessage = "Done." }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Read my habits"), default);

        result.IsSuccess.Should().Be(exempt);
        await _aiIntentService.Received(exempt ? 2 : initialCeiling ? 0 : 1)
            .ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvalUnmetered_AllowsCertifiedCorrectionPastRequestTokenCeiling()
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraEvalUnmetered]);
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        SetupAiResponse(ToolResponse(tool.Name, "rejected", WithMinuteSeconds(ProductionAdaptationArguments)) with
        {
            ReportedTokenCount = AppConstants.MaxAiRequestTotalTokens
        });
        var candidate = ToolResponse(tool.Name, "candidate", ProductionAdaptationArguments);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(candidate),
                Result.Success(new AiResponse { TextMessage = "Done." }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create the adaptation habit."), default);

        result.IsSuccess.Should().BeTrue();
        gateway.Requests.Should().HaveCount(2);
        gateway.Responses[0].Should().BeOfType<RejectedBeforeDispatchResponse>();
        gateway.Responses[1].Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
    }
}
