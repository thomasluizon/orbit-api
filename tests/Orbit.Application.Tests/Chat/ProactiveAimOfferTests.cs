using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "I want to run five kilometers. Show me the goals I already have.", "Run five kilometers")]
    [InlineData("pt-BR", "Quero correr cinco quilômetros. Mostre as metas que já tenho.", "Correr cinco quilômetros")]
    [InlineData("en", "I want to run five kilometers. Show me the goals I already have.", "My first race")]
    [InlineData("pt-BR", "Quero correr 5 km. Mostre as metas que já tenho.", "Primeira corrida")]
    public async Task AimOffer_EquivalentActiveGoalSuppressesTheDeclaredAim(string locale, string message, string title)
    {
        var goal = Goal.Create(UserId, title, targetValue: 5, unit: "km").Value;

        var card = await AimOfferTurnAsync(locale, message, [goal], withHabitRead: false);

        card.Read!.NextAction?.OfferKey.Should().NotBe("aim:account");
    }

    [Theory]
    [InlineData("en", "I want to run five kilometers. Show me the goals I already have.", false)]
    [InlineData("pt-BR", "Quero correr cinco quilômetros. Mostre as metas que já tenho.", false)]
    [InlineData("en", "I want to run five kilometers. Show me the goals I already have.", true)]
    [InlineData("pt-BR", "Quero correr cinco quilômetros. Mostre as metas que já tenho.", true)]
    public async Task AimOffer_DeclaredAimSurvivesWithoutAnEquivalentGoal(string locale, string message, bool withHabitRead)
    {
        var other = Goal.Create(UserId, "Read twelve books", targetValue: 12, unit: "books").Value;

        var card = await AimOfferTurnAsync(locale, message, [other], withHabitRead);

        card.Read!.NextAction.Should().NotBeNull("the declared aim on the goal read keeps its action");
        card.Read.NextAction!.OfferKey.Should().Be("aim:account");
    }

    private async Task<GoalListCard> AimOfferTurnAsync(string locale, string message, Goal[] goals, bool withHabitRead)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>()).Returns(goals);
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns(goals);
        SetupOfferHabits();
        var goalsTool = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var habitsTool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(goalsTool, habitsTool);
        UseProductionReadExecutor(goalsTool, habitsTool);
        var goalRead = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "ProactiveOffers", "r9i-goals.json")))!.AsObject();
        goalRead["orbit_offer_intent"] = "aim";
        AiToolCall[] calls = withHabitRead
            ? [new(goalsTool.Name, "goals", ParseArguments(goalRead.ToJsonString())),
               new(habitsTool.Name, "habits", ParseArguments("""{"date":"today","orbit_read_complete":true,"orbit_offer_intent":"none"}"""))]
            : [new(goalsTool.Name, "goals", ParseArguments(goalRead.ToJsonString()))];
        _offers.IssuedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AiOfferFact>());
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<AiToolRequest>().SystemPrompt == ProactiveOfferPlanner.SelectionInstruction
                ? Result.Failure<AiResponse>("selector_not_expected")
                : Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = TestConversationContext }));

        var result = await handler.Handle(new(UserId, message, ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        return result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Block).OfType<GoalListCard>().Should().ContainSingle().Which;
    }
}
