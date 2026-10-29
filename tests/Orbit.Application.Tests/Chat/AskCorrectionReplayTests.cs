using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task AskCorrectionReplay_CapturedUnsupportedFormCanRetryAsSupportedChoice()
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(GateSevenReplay("composition-choice-en-r1"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(HonestyReplay("unsupported-question-r1")));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Change my habit.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var choice = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        choice.Question.Should().Be("Which habit would you like to change, and what would you like to change?");
        choice.AllowFreeText.Should().BeTrue();
        choice.Options.Select(option => option.Label).Should().Equal("Water", "Walk");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskCorrectionReplay_InvalidRetryFallsBackToTextAfterTwoAttempts()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var invalid = GateSevenReplay("composition-choice-en-r1");
        SetupAiResponse(invalid);
        const string question = "Which habit would you like to change, and what would you like to change?";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(invalid), Result.Success(new AiResponse { TextMessage = question }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Change my habit.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(question);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.Received(2).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

}
