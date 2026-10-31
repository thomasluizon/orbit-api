using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", 3)]
    [InlineData("pt-BR", 2)]
    [InlineData("pt-BR", 3)]
    public async Task G11_LegacyCapturedEmptyAskIsNeverOfferedOrExecutedAndReadsStillAnswer(string language, int repeat)
    {
        SetupExistingParents(language);
        EnableComposition();
        var pt = language == "pt-BR";
        var habit = CreateHabit(pt ? "Beber água" : "Water");
        var goal = Goal.Create(UserId, pt ? "Hidratação constante" : "Consistent hydration", 10, pt ? "sessões" : "sessions").Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        var habits = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var goals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var ask = FakeTool("ask");
        ask.IsReadOnly.Returns(true);
        var handler = CreateHandler(ask, habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupAiResponse(CapturedCompositionPlan($"prod-r1-composition-compound-legacy-{language}-r{repeat}"));
        ContinueWithReadDirective("[[orbit:habits]]\n[[orbit:goals]]");
        var client = new AgentClientContext(SupportsParts: false, PartKinds: [], SupportsHabitListCard: true,
            SupportsGoalListCard: true, Locale: language);

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().BeNull();
        result.Value.AiMessage.Should().Contain(habit.Title).And.Contain(goal.Title);
        var request = _aiIntentService.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "SendWithToolsAsync")
            .GetArguments()[0].Should().BeOfType<AiToolRequest>().Subject;
        request.ToolDeclarations.Select(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString())
            .Should().BeEquivalentTo(["query_habits", "query_goals"]);
        await ask.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        AssertStoredComposition(result.Value, client, language);
    }
}
