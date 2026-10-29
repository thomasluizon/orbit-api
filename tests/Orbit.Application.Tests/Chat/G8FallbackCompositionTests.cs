using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", false)]
    [InlineData("pt-BR", true)]
    public async Task G8R2_CapturedSummaryFallbackUsesAccountToday(string language, bool streamed)
    {
        SetupExistingParents(language);
        EnableComposition();
        var today = new DateOnly(2026, 10, 29);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var summary = new GetDailySummaryTool(_mediator, _userDateService);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.Arg<GetDailySummaryQuery>();
            return Result.Success(new DailySummaryResponse("", "", false,
                new DaySummaryCard(query.DateFrom, 2, 0, 0, 0, 4, DateTo: query.DateTo)));
        });
        var goals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(summary, goals);
        UseProductionReadExecutor(summary, goals);
        SetupAiResponse(CapturedCompositionPlan("g8r2-composition-compound-missing-kind-pt-BR-r2"));
        ContinueWithReadDirective("[[orbit:read:capture_0]]\n[[orbit:read:capture_1]]");
        var client = new AgentClientContext(SupportsParts: true, PartKinds: [], Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client,
            StreamSink: streamed ? item => { events.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded);
        result.Value.Operations!.Select(operation => operation.OperationId).Should().Equal("get_daily_summary", "query_goals");
        result.Value.Parts.Should().HaveCount(2).And.AllBeOfType<ChatTextPart>();
        result.Value.Parts![0].Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(language == "pt-BR"
            ? "Hoje: Concluídos / agendados: 0/2" : "Today: Completed / scheduled: 0/2");
        result.Value.AiMessage.Should().Be(string.Join("\n\n", result.Value.Parts.Cast<ChatTextPart>().Select(part => part.Text)));
        events.Should().NotContain(item => item.Type == "delta");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        AssertStoredComposition(result.Value, client, language);
        await _mediator.Received(1).Send(Arg.Is<GetDailySummaryQuery>(query => query.DateFrom == today && query.DateTo == today
            && !query.GenerateNarrative), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false, false)]
    [InlineData("en", false, true)]
    [InlineData("pt-BR", false, false)]
    [InlineData("pt-BR", false, true)]
    [InlineData("pt-BR", true, false)]
    [InlineData("pt-BR", true, true)]
    public async Task G8_CapturedCompoundFallbackKeepsFactsWithoutTitles(string language, bool month, bool supported)
    {
        SetupExistingParents(language);
        EnableComposition();
        var pt = language == "pt-BR";
        var habit = CreateHabit(pt ? "Beber água" : "Water");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        var goal = Goal.Create(UserId, pt ? "Hidratação constante" : "Consistent hydration", 10, pt ? "sessões" : "sessions").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        IAiTool habits = month ? G6HabitMonthTool() : new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var goals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupAiResponse(CapturedCompositionPlan("g8-composition-compound-missing-kind-" + language + (month ? "-r2" : "-r1")));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: supported ? [month ? "habitMonth" : "habitList", "goalList"] : [],
            SupportsHabitListCard: true, SupportsGoalListCard: true, Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded);
        if (supported)
        {
            result.Value.Parts!.OfType<ChatBlockPart>().Select(block => block.Kind).Should().Equal(month ? "habitMonth" : "habitList", "goalList");
            result.Value.AiMessage.Should().BeNullOrEmpty();
        }
        else
        {
            result.Value.Parts.Should().HaveCount(2).And.AllBeOfType<ChatTextPart>();
            result.Value.AiMessage.Should().Contain(habit.Title).And.Contain(goal.Title).And.Contain("0/10")
                .And.NotMatchRegex(@"(?m)^(All habits|Todos os hábitos|Habit calendar|Calendário de hábitos)(:|$)");
            string.Join("\n\n", result.Value.Parts!.Cast<ChatTextPart>().Select(part => part.Text)).Should().Be(result.Value.AiMessage);
        }
        events.Should().NotContain(item => item.Type == "delta");
        await AssertNoCompositionContinuation();
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", false)]
    [InlineData("pt-BR", true)]
    public async Task G8_CapturedCompleteWriteKeepsLocalizedLegacyPreview(string language, bool streamed)
    {
        SetupExistingParents(language);
        EnableComposition();
        var create = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(create);
        var held = HoldReplayWrites(create);
        SetupAiResponse(CapturedCompositionPlan("g8-legacy-create-pt-r1"));
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Prepare the requested habit.", ClientContext: new(),
            StreamSink: streamed ? item => { events.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().BeNull();
        result.Value.AiMessage.Should().Be((language == "en" ? "Review the proposed changes before approving." : "Revise as alterações propostas antes de aprovar.") + "\n- Meditar");
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle().Which.EntityName.Should().Be("Meditar");
        held.Should().ContainSingle();
        result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        if (streamed) events.Last(item => item.Type == "delta").Text.Should().Be(result.Value.AiMessage);
        await AssertNoCompositionContinuation();
        AssertStoredComposition(result.Value, new(), language);
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Habits.Commands.CreateHabitCommand>(), Arg.Any<CancellationToken>());
    }
}
