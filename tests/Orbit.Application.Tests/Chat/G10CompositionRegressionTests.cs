using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task G10_LegacyCompoundListsGroundedItemsAndOmitsEmptySections(string language, bool emptyGoals)
    {
        SetupExistingParents(language);
        EnableComposition();
        var pt = language == "pt-BR";
        var habits = new[] { CreateHabit(pt ? "Beber água" : "Water"), CreateHabit(pt ? "Caminhada" : "Walk") };
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns(habits);
        var goal = Goal.Create(UserId, pt ? "Hidratação constante" : "Consistent hydration", 10, pt ? "sessões" : "sessions").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns(emptyGoals ? [] : [goal]);
        var queryHabits = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var queryGoals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(queryHabits, queryGoals);
        UseProductionReadExecutor(queryHabits, queryGoals);
        SetupAiResponse(CapturedCompositionPlan(pt ? "g10-composition-compound-legacy-pt-BR-r2" : "g10-composition-compound-legacy-en-r3"));
        ContinueWithReadDirective(pt ? "Aqui estão seus hábitos e metas:\n\n**Hábitos**\n[[orbit:habits]]\n**Metas**\n[[orbit:goals]]"
            : "Here are your habits and goals.\n\n**Habits**\n[[orbit:habits]]\n**Goals**\n[[orbit:goals]]");
        var client = new AgentClientContext(SupportsParts: false, SupportsHabitListCard: true, SupportsGoalListCard: true, Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().BeNull();
        foreach (var habit in habits) result.Value.AiMessage.Should().Contain(habit.Title);
        if (emptyGoals) result.Value.AiMessage.Should().NotContain(pt ? "Metas" : "Goals");
        else result.Value.AiMessage.Should().Contain(goal.Title).And.Contain("0/10");
        result.Value.AiMessage.Should().NotContain("[[orbit:").And.NotContain("**");
        AssertStoredComposition(result.Value, client, language);
        events.Where(item => item.Type == "delta").Should().OnlyContain(item => item.Text == result.Value.AiMessage);
    }

    [Fact]
    public async Task G10_QueryAndReviewGoalsRenderOneFamilyBlock()
    {
        SetupExistingParents("pt-BR");
        EnableComposition();
        var goal = Goal.Create(UserId, "Hidratação constante", 10, "sessões").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        var query = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var review = new GoalReviewTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(query, review);
        UseProductionReadExecutor(query, review);
        SetupAiResponse(CapturedCompositionPlan("g10-composition-goals-pt-BR-r1"));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["goalList"], SupportsGoalListCard: true, Locale: "pt-BR");

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Operations.Should().HaveCount(2);
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        block.Block.Should().BeOfType<GoalListCard>().Which.Items.Should().ContainSingle().Which.Title.Should().Be(goal.Title);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await AssertNoCompositionContinuation();
        AssertStoredComposition(result.Value, client, "pt-BR");
    }
}
