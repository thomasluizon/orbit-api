using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Application.Chat.Models;
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
    private readonly IFeatureFlagService _featureFlags = Substitute.For<IFeatureFlagService>();
    private static readonly Guid UserId = Guid.NewGuid();

    public AstraBlockAnswerServiceTests()
    {
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<PreparedAstraBlockAnswer>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<PreparedAstraBlockAnswer>>>>()(default));
        _quota.TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.MarkAnsweredAsync(Arg.Any<Guid>(), UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private AstraBlockAnswerService Service => new(_store, _work, _quota, _executor, _users, _analytics, _featureFlags,
        Substitute.For<IPendingAgentOperationStore>());
    private IssuedAstraBlock Choice(bool multi = false, bool free = false)
    {
        var block = IssuedAstraBlock.Create(UserId, new("choice", "Which time?", [new("a", "Morning"), new("b", "Evening")], [], multi, free));
        _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        return block;
    }
    private static ProcessUserChatCommand Request(Guid id, string[] ids, string? text = null) => new(UserId, "Selection",
        BlockAnswer: new AstraBlockAnswer(id, ids, text));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_RequiresCurrentlyAdvertisedKinds(bool supportsParts)
    {
        var block = Choice();
        var answer = new AstraBlockAnswer(block.Id, ["a"]);
        block.MarkAnswered(block.CreatedAtUtc);
        typeof(IssuedAstraBlock).GetProperty(nameof(block.AnswerJson))!.SetValue(block, IssuedAstraBlock.SerializeAnswer(answer));
        typeof(IssuedAstraBlock).GetProperty(nameof(block.ResponseJson))!.SetValue(block,
            JsonSerializer.Serialize(new ChatResponse(null, [], Parts: [new ChatBlockPart("habitList", new HabitListCard("all", []))]),
                ChatStreamEvent.SerializerOptions));
        var result = await Service.PrepareAsync(Request(block.Id, ["a"]) with
        { ClientContext = new(SupportsParts: supportsParts, PartKinds: ["unknown"], SupportsHabitListCard: true) }, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Response!.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.Response.AiMessage.Should().NotBeNullOrWhiteSpace();
    }

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
    public async Task ChoiceAtDailyLimit_ClaimsAnswerButDoesNotRecordResponse()
    {
        var block = Choice();
        _quota.TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Daily limit"));
        var result = await Service.PrepareAsync(Request(block.Id, ["a"]), default);
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        result.Error.Should().Be("Daily limit");
        await _store.Received(1).MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _quota.Received(1).TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().RecordAnswerAsync(default, default, default, string.Empty, null, null, default);
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
    [InlineData("create_habits", "parts")]
    [InlineData("log_habits", "parts")]
    [InlineData("skip_habits", "parts")]
    [InlineData("create_habits", "legacy")]
    [InlineData("log_habits", "legacy")]
    [InlineData("skip_habits", "legacy")]
    [InlineData("create_habits", "disabled")]
    [InlineData("log_habits", "disabled")]
    [InlineData("skip_habits", "disabled")]
    [InlineData("create_habits", "prior")]
    public async Task PickListAtDailyLimit_ReturnsOnePreviewOfExactlyTheStoredSelection(string action, string mode)
    {
        var items = Enumerable.Range(0, 4).Select(i => new AstraBlockItem(i.ToString(), "Habit " + i,
            action == "create_habits" ? JsonSerializer.SerializeToElement(new { title = "Habit " + i })
                : JsonSerializer.SerializeToElement(new { habit_id = Guid.NewGuid(), date = "2026-04-03" }))).ToArray();
        var block = IssuedAstraBlock.Create(UserId, new("pickList", "Choose", [], items, true, Action: action));
        _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _quota.TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Daily limit"));
        AgentExecuteOperationRequest? captured = null;
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Preview",
            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc,
            Items: [new("1", null, "Habit 1", [], "fingerprint1"), new("3", null, "Habit 3", [], "fingerprint3")]);
        _executor.ExecuteAsync(Arg.Do<AgentExecuteOperationRequest>(value => captured = value), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new("bulk_" + action, "bulk_" + action, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending));
        _featureFlags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(mode switch
        {
            "disabled" => [FeatureFlagKeys.AstraBlockFirst, FeatureFlagKeys.AstraPartsDisabled],
            "prior" => [],
            _ => [FeatureFlagKeys.AstraBlockFirst]
        });
        var result = await Service.PrepareAsync(Request(block.Id, ["3", "1"]) with
        { ClientContext = new(SupportsParts: mode != "legacy") }, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Response!.PendingOperations.Should().ContainSingle().Which.Should().Be(pending);
        if (mode == "parts")
        {
            result.Value.Response.AiMessage.Should().BeNullOrEmpty();
            result.Value.Response.Parts.Should().BeEmpty();
        }
        else if (mode == "prior")
            result.Value.Response.AiMessage.Should().Be("Nothing has been saved.");
        else
        {
            result.Value.Response.AiMessage.Should().Contain("Habit 1").And.Contain("Habit 3").And.Contain("approving");
            result.Value.Response.Parts.Should().BeNull();
        }
        captured!.ConfirmationToken.Should().BeNull();
        captured.IncludeChangePreview.Should().BeTrue();
        var arguments = captured.Arguments.GetProperty(action == "create_habits" ? "habits" : "revised_items");
        arguments.EnumerateArray().Select(item => item.GetRawText()).Should().Equal(items[1].Arguments.GetRawText(), items[3].Arguments.GetRawText());
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
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

    [Fact]
    public async Task ConcurrentCrisisAnswers_ExactlyOneSucceedsWithoutQuota()
    {
        var block = Choice(free: true);
        _quota.TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure("Daily limit"));
        var claims = 0;
        _store.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref claims) == 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            Service.PrepareAsync(Request(block.Id, [], "I want to hurt myself"), default))));

        results.Should().ContainSingle(result => result.IsSuccess).Which.Value.Crisis.Should().Be(CrisisLocales.English);
        results.Should().ContainSingle(result => result.ErrorCode == ErrorCodes.AstraBlockAnswered);
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }

    [Theory]
    [InlineData(false, "a", ErrorCodes.AstraBlockFreeTextDisabled)]
    [InlineData(true, "unknown", ErrorCodes.AstraBlockUnknownSelection)]
    public async Task InvalidCrisisAnswer_IsRejectedBeforeClaimOrQuota(bool allowFreeText, string selectedId, string code)
    {
        var block = Choice(free: allowFreeText);

        var result = await Service.PrepareAsync(Request(block.Id, [selectedId], "I want to hurt myself"), default);

        result.ErrorCode.Should().Be(code);
        await _store.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_WaitingOperationWithoutRefresh_KeepsTheStoredOperation(bool throws)
    {
        var block = Choice();
        var answer = new AstraBlockAnswer(block.Id, ["a"]);
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Preview",
            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc,
            Items: [new("a", null, "Walk", [], "fingerprint")]);
        block.MarkAnswered(block.CreatedAtUtc);
        typeof(IssuedAstraBlock).GetProperty(nameof(block.AnswerJson))!.SetValue(block, IssuedAstraBlock.SerializeAnswer(answer));
        typeof(IssuedAstraBlock).GetProperty(nameof(block.ResponseJson))!.SetValue(block,
            JsonSerializer.Serialize(new ChatResponse("Nothing has been saved.", [], PendingOperations: [pending]), ChatStreamEvent.SerializerOptions));
        var operations = Substitute.For<IPendingAgentOperationStore>();
        operations.IsAwaitingConfirmation(UserId, pending.Id).Returns(true);
        operations.GetExecution(UserId, pending.Id).Returns(new PendingAgentOperationExecution(pending.Id,
            "habits", "bulk_create_habits", JsonSerializer.SerializeToElement(new { habits = new[] { new { title = "Walk" } } }),
            AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation));
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(UserId, "bulk_create_habits", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(_ => throws ? Task.FromException<Result<PendingOperationChangePreview?>>(new InvalidOperationException("preview failed"))
                : Task.FromResult(Result.Success<PendingOperationChangePreview?>(null)));
        var service = new AstraBlockAnswerService(_store, _work, _quota, _executor, _users, _analytics,
            _featureFlags, operations, previewer: previewer);

        var result = await service.PrepareAsync(Request(block.Id, ["a"]), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Response!.PendingOperations.Should().ContainSingle().Which.Should().BeEquivalentTo(pending);
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task StaleLegacyAnswer_IsNeverReleasedOrRefunded()
    {
        var block = Choice();
        block.MarkAnswered(block.CreatedAtUtc);
        var service = new AstraBlockAnswerService(_store, _work, _quota, _executor, _users, _analytics,
            _featureFlags, Substitute.For<IPendingAgentOperationStore>(), claimLifetime: TimeSpan.Zero);

        var result = await service.PrepareAsync(Request(block.Id, ["a"]), default);

        result.ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
        await _store.DidNotReceiveWithAnyArgs().ReleaseAnswerAsync(default, default, default, default);
        await _quota.DidNotReceiveWithAnyArgs().RefundAiMessage(default, default, default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRecordedAnswer_RefundsOnlyAReleasedClaim(bool released)
    {
        var block = Choice();
        block.MarkAnswered(block.CreatedAtUtc);
        var reservedDate = new DateOnly(2026, 4, 3);
        typeof(IssuedAstraBlock).GetProperty(nameof(block.AnswerJson))!.SetValue(block,
            IssuedAstraBlock.SerializeAnswer(new(block.Id, ["a"])));
        typeof(IssuedAstraBlock).GetProperty(nameof(block.ReservedAllowanceDate))!.SetValue(block, reservedDate);
        _store.ReleaseAnswerAsync(block.Id, UserId, block.CreatedAtUtc, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (released)
                typeof(IssuedAstraBlock).GetProperty(nameof(block.AnsweredAtUtc))!.SetValue(block, null);
            return released;
        });
        _quota.RefundAiMessage(UserId, reservedDate, _work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var service = new AstraBlockAnswerService(_store, _work, _quota, _executor, _users, _analytics,
            _featureFlags, Substitute.For<IPendingAgentOperationStore>(), claimLifetime: TimeSpan.Zero);

        var result = await service.PrepareAsync(Request(block.Id, ["a"]), default);

        result.IsSuccess.Should().Be(released);
        await _quota.Received(released ? 1 : 0).RefundAiMessage(UserId, reservedDate, _work, Arg.Any<CancellationToken>());
    }

}
