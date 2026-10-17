using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class AstraBlockAnswerServiceTests
{
    private readonly IIssuedAstraBlockStore _store = Substitute.For<IIssuedAstraBlockStore>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly IPayGateService _quota = Substitute.For<IPayGateService>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IProductAnalytics _analytics = Substitute.For<IProductAnalytics>();
    private static readonly Guid UserId = Guid.NewGuid();

    public AstraBlockAnswerServiceTests()
    {
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<PreparedAstraBlockAnswer>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<PreparedAstraBlockAnswer>>>>()(default));
        _quota.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _quota.TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.MarkAnsweredAsync(Arg.Any<Guid>(), UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private AstraBlockAnswerService Service => new(_store, _work, _quota, _executor, _users, _analytics);
    private IssuedAstraBlock Choice(bool multi = false, bool free = false)
    {
        var block = IssuedAstraBlock.Create(UserId, new("choice", "Which time?", [new("a", "Morning"), new("b", "Evening")], [], multi, free));
        _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        return block;
    }
    private static ProcessUserChatCommand Request(Guid id, string[] ids, string? text = null) => new(UserId, "Selection",
        BlockAnswer: new AstraBlockAnswer(id, ids, text));

    [Theory]
    [InlineData("unknown", "ASTRA_BLOCK_UNKNOWN_SELECTION")]
    [InlineData("many", "ASTRA_BLOCK_SINGLE_SELECTION")]
    [InlineData("none", "ASTRA_BLOCK_EMPTY_SELECTION")]
    [InlineData("text", "ASTRA_BLOCK_FREE_TEXT_DISABLED")]
    [InlineData("long", "ASTRA_BLOCK_FREE_TEXT_TOO_LONG")]
    [InlineData("duplicate", "ASTRA_BLOCK_INVALID_SELECTION")]
    public async Task InvalidAnswer_HasDistinctCodeAndChangesNothing(string kind, string code)
    {
        var block = Choice(multi: kind == "none");
        var ids = kind switch { "unknown" => new[] { "x" }, "many" => ["a", "b"], "none" => [], "duplicate" => ["a", "a"], _ => ["a"] };
        var text = kind == "text" ? "Other" : kind == "long" ? new string('x', 501) : null;
        var result = await Service.PrepareAsync(Request(block.Id, ids, text), default);
        result.ErrorCode.Should().Be(code);
        await _store.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task ForeignOrMissingBlock_IsNotFound()
    {
        (await Service.PrepareAsync(Request(Guid.NewGuid(), ["a"]), default)).ErrorCode.Should().Be(ErrorCodes.AstraBlockNotFound);
        await _store.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
    }

    [Fact]
    public async Task AlreadyAnswered_IsConflict()
    {
        var block = Choice();
        block.MarkAnswered(block.CreatedAtUtc);
        (await Service.PrepareAsync(Request(block.Id, ["a"]), default)).ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        await _store.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
    }

    [Fact]
    public async Task ChoiceAtDailyLimit_IsNotClaimedOrCharged()
    {
        var block = Choice();
        _quota.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Daily limit"));
        (await Service.PrepareAsync(Request(block.Id, ["a"]), default)).ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _store.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }

    [Fact]
    public async Task Choice_UsesQuestionStoredLabelsAndFreeTextAndChargesOnce()
    {
        var block = Choice(free: true);
        var result = await Service.PrepareAsync(Request(block.Id, ["b"], "After dinner"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Message.Should().Be("Which time?\nEvening, After dinner");
        result.Value.Response.Should().BeNull();
        await _quota.Received(1).TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>());
        await _executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData("create_habits")]
    [InlineData("log_habits")]
    [InlineData("skip_habits")]
    public async Task PickListAtDailyLimit_ReturnsOnePreviewOfExactlyTheStoredSelection(string action)
    {
        var items = Enumerable.Range(0, 4).Select(i => new AstraBlockItem(i.ToString(), "Habit " + i,
            action == "create_habits" ? JsonSerializer.SerializeToElement(new { title = "Habit " + i })
                : JsonSerializer.SerializeToElement(new { habit_id = Guid.NewGuid(), date = "2026-04-03" }))).ToArray();
        var block = IssuedAstraBlock.Create(UserId, new("pickList", "Choose", [], items, true, Action: action));
        _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _quota.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Daily limit"));
        AgentExecuteOperationRequest? captured = null;
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Preview",
            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc,
            Items: [new("1", null, "Habit 1", [], "fingerprint1"), new("3", null, "Habit 3", [], "fingerprint3")]);
        _executor.ExecuteAsync(Arg.Do<AgentExecuteOperationRequest>(value => captured = value), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new("bulk_" + action, "bulk_" + action, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending));
        var result = await Service.PrepareAsync(Request(block.Id, ["3", "1"]), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Response!.PendingOperations.Should().ContainSingle().Which.Should().Be(pending);
        result.Value.Response.AiMessage.Should().Be("Nothing has been saved.");
        captured!.ConfirmationToken.Should().BeNull();
        captured.IncludeChangePreview.Should().BeTrue();
        var arguments = captured.Arguments.GetProperty(action == "create_habits" ? "habits" : "revised_items");
        arguments.EnumerateArray().Select(item => item.GetRawText()).Should().Equal(items[1].Arguments.GetRawText(), items[3].Arguments.GetRawText());
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _quota.DidNotReceiveWithAnyArgs().CanSendAiMessage(default, default);
        await _executor.Received(1).ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConcurrentValidAnswers_ExactlyOneReservesAllowance()
    {
        var block = Choice();
        var claims = 0;
        _store.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref claims) == 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Service.PrepareAsync(Request(block.Id, ["a"]), default))));
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.ErrorCode == ErrorCodes.AstraBlockAnswered);
        await _quota.Received(1).TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>());
    }
}
