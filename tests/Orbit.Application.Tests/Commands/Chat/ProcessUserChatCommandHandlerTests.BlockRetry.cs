using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private IssuedAstraBlock RetryChoice()
    {
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], [], AllowFreeText: true));
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (block.AnsweredAtUtc.HasValue)
                    return false;
                block.MarkAnswered(call.Arg<DateTime>());
                return true;
            });
        _blockStore.RecordAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(),
                Arg.Any<DateOnly?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                block.RecordAnswer(System.Text.Json.JsonSerializer.Deserialize<AstraBlockAnswer>(call.ArgAt<string>(3))!, call.ArgAt<DateOnly?>(4));
                if (call.ArgAt<string?>(5) is { } response)
                    block.CompleteAnswer(response);
                return Task.CompletedTask;
            });
        _blockStore.CompleteAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { block.CompleteAnswer(call.Arg<string>()); return true; });
        _blockStore.ReleaseAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => { block.ReleaseAnswer(); return true; });
        return block;
    }

    [Fact]
    public async Task ChoiceAnswer_FailedAiReply_RetrySucceedsWithOneDailyCharge()
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(ErrorMessages.AiUnavailable),
                Result.Success(new AiResponse { TextMessage = "Evening works." }));
        var handler = CreateHandler(quota);
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]));

        (await handler.Handle(request, default)).IsFailure.Should().BeTrue();
        user.GetAiMessagesUsedToday(Today).Should().Be(0);
        block.AnsweredAtUtc.Should().BeNull();
        var retry = await handler.Handle(request, default);

        retry.IsSuccess.Should().BeTrue();
        retry.Value.AiMessage.Should().Be("Evening works.");
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
    }

    [Fact]
    public async Task ChoiceAnswer_LostReply_ReplaysStoredResponseAndRejectsDifferentAnswer()
    {
        var user = User.Create("Alex", "retry@example.com").Value;
        var quota = CreateRealPayGate(user);
        var block = RetryChoice();
        SetupAiResponse(new AiResponse { TextMessage = "Evening works." });
        var handler = CreateHandler(quota);
        var request = new ProcessUserChatCommand(UserId, "Evening", BlockAnswer: new(block.Id, ["b"], "After dinner"));

        var first = await handler.Handle(request, default);
        var replay = await handler.Handle(request, default);

        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(first.Value);
        user.GetAiMessagesUsedToday(Today).Should().Be(1);
        (await handler.Handle(request with { BlockAnswer = new(block.Id, ["a"], "After dinner") }, default))
            .ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        (await handler.Handle(request with { BlockAnswer = new(block.Id, ["b"], "Before dinner") }, default))
            .ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
