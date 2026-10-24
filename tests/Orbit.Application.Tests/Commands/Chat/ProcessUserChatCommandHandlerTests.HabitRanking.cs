using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private async Task<HabitRankingResponse> ProduceRankingAsync()
    {
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Read", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        habit.Log(Today, advanceDueDate: false);
        var tokens = new HabitRankingTokenService(Options.Create(new JwtSettings
        {
            SecretKey = "ranking-chat-unit-test-signing-secret",
            Issuer = "test",
            Audience = "test"
        }));
        var handler = new GetHabitRankingQueryHandler(_habitRepo, _userDateService, tokens,
            _productAnalytics, NullLogger<GetHabitRankingQueryHandler>.Instance);
        _habitRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns([habit]);
        return (await handler.Handle(new GetHabitRankingQuery(UserId, Today, Today.AddDays(1)), default)).Value;
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task Handle_HabitRanking_NegotiatesBothCapabilities_AndKeepsReadableFallback(bool parts, bool kind, bool disabled)
    {
        SetupUserAndPayGate();
        var response = await ProduceRankingAsync();
        var tool = new GetHabitRankingTool(_mediator);
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(response));
        if (disabled)
            _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns([FeatureFlagKeys.AstraPeriodBlocksDisabled]);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall(tool.Name, "ranking", ParseArguments("""{"date_from":"2026-04-03","date_to_exclusive":"2026-04-04","refresh":null}"""))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "[[orbit:ranking]]" }));

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Rank my completions",
            ClientContext: new AgentClientContext(SupportsParts: parts, PartKinds: kind ? ["habitRanking"] : [])), default);

        result.IsSuccess.Should().BeTrue();
        var blocks = result.Value.Parts?.OfType<ChatBlockPart>().ToList() ?? [];
        if (parts && kind && !disabled)
        {
            var block = blocks.Should().ContainSingle().Which;
            block.Kind.Should().Be("habitRanking");
            block.Block.Should().BeOfType<HabitRankingCard>().Which.Ranking.Should().Be(response);
        }
        else
        {
            blocks.Should().BeEmpty();
            result.Value.AiMessage.Should().Contain("Read: 1").And.Contain("2026-04-03");
        }
        result.Value.AiMessage.Should().NotContain("[[orbit:");
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "habit_ranking_block", "Free",
            Arg.Is<IReadOnlyDictionary<string, object>>(p => p["kind"].Equals("habitRanking")
                && p["metric"].Equals("completion_log_records") && p["result_count"].Equals(1)
                && p["complete"].Equals(true) && p["build_failure"].Equals(false)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_HabitRanking_SurvivesNarrativeProviderFailure(bool supports)
    {
        SetupUserAndPayGate();
        var response = await ProduceRankingAsync();
        var tool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: response));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall(tool.Name, "ranking", ParseArguments("{}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>("Provider offline"));

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Show my ranking",
            ClientContext: new AgentClientContext(SupportsParts: supports, PartKinds: ["habitRanking"])), default);

        result.IsSuccess.Should().BeTrue();
        if (supports)
            result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
                .Should().BeOfType<HabitRankingCard>().Which.Ranking.Should().Be(response);
        else
            result.Value.AiMessage.Should().Contain("Read: 1");
        result.Value.Actions.Should().BeEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitRanking_BuilderFailureReturnsReadableFailure_AndTelemetry()
    {
        SetupUserAndPayGate();
        var valid = await ProduceRankingAsync();
        var invalid = valid with { Query = valid.Query with { Metric = "unsupported" } };
        var tool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: invalid));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall(tool.Name, "ranking", ParseArguments("{}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "[[orbit:ranking]]" }));

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Rank my habits",
            ClientContext: new AgentClientContext(SupportsParts: true, PartKinds: ["habitRanking"])), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().NotBeNullOrWhiteSpace();
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "habit_ranking_block", "Free",
            Arg.Is<IReadOnlyDictionary<string, object>>(p => p["build_failure"].Equals(true)));
    }
}
