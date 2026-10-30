using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private readonly IConversationOfferStore _offers = Substitute.For<IConversationOfferStore>();

    [Theory]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    [InlineData("en", false, true)]
    [InlineData("en", true, false)]
    public async Task ProactiveReadReplay_SelectsOneGroundedActionWithoutWriting(string locale, bool supports, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, locale == "en" ? "Water" : "Água",
            FrequencyUnit.Day, 1, Today, CreatedAtUtc: Today.AddDays(-14).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        SetupOfferHabits(habit);
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupOfferSelection(tool.Name, "log");
        var client = OfferClient(locale) with { SupportsProactiveOffers = supports };

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var block = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        var card = block.Block.Should().BeOfType<HabitListCard>().Subject;
        card.Items.Should().ContainSingle().Which.Id.Should().Be(habit.Id.ToString());
        if (supports)
        {
            card.Read!.NextAction!.Kind.Should().Be("preparePreview");
            card.Read.NextAction.EntityId.Should().Be(habit.Id);
            card.Read.NextAction.Date.Should().Be(Today);
            card.Read.NextAction.Label.Should().Be(locale == "en" ? "Log this habit" : "Registrar hábito");
            await _offers.Received(1).IssueAsync(UserId, client.ConversationId!.Value,
                Arg.Is<AstraOfferFact>(fact => fact.QueryId == card.Read.QueryId && fact.Revision == card.Read.Revision), Arg.Any<CancellationToken>());
        }
        else
        {
            card.Read!.NextAction.Should().BeNull();
            await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>());
        }
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.FollowUps.Should().BeNullOrEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("tools")]
    [InlineData("malformed")]
    [InlineData("timeout")]
    public async Task ProactiveReadReplay_InvalidSelectionRetainsTheReadableBlock(string mode)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupOfferSelection(tool.Name, "smallStep", mode);
        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: OfferClient("en")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        var card = result.Value.Parts!.OfType<ChatBlockPart>().Single().Block.Should().BeOfType<HabitListCard>().Subject;
        card.Read!.NextAction.Should().BeNull();
        await _offers.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default!, default);
    }

    [Fact]
    public async Task ProactiveReadReplay_DismissalSurvivesNewHandlerAndTruncatedHistory()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var dismissed = new HashSet<string>(StringComparer.Ordinal);
        _offers.DismissedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => dismissed);
        _offers.DismissAsync(UserId, Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => { dismissed.UnionWith(call.Arg<IReadOnlyList<string>>()); return Task.CompletedTask; });
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var first = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupOfferSelection(tool.Name, "smallStep");
        var client = OfferClient("en");
        var response = await first.Handle(new(UserId, "Show the requested records.", ClientContext: client), default);
        var offered = ((HabitListCard)response.Value.Parts!.OfType<ChatBlockPart>().Single().Block).Read!.NextAction!;
        var next = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupOfferSelection(tool.Name, "smallStep");
        var result = await next.Handle(new(UserId, "Show the requested records.",
            ClientContext: client with { DismissedOfferKeys = [offered.OfferKey] }, History: []), default);
        result.IsSuccess.Should().BeTrue();
        ((HabitListCard)result.Value.Parts!.OfType<ChatBlockPart>().Single().Block).Read!.NextAction.Should().BeNull();
        dismissed.Should().Contain(offered.OfferKey);
    }

    [Fact]
    public async Task ProactiveReadReplay_PreservesBothSubjectsAndAttachesOnlyOneOffer()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Water", FrequencyUnit.Day, 1, Today,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        SetupOfferHabits(habit);
        var habits = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var goals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupOfferSelection(habits.Name, "log", secondTool: goals.Name);
        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: OfferClient("en")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        var blocks = result.Value.Parts!.OfType<ChatBlockPart>().ToArray();
        blocks.Select(block => block.Kind).Should().Equal("habitList", "goalList");
        blocks.Count(block => ReadBlockTelemetry.Describe(block.Block).Read?.NextAction is not null).Should().Be(1);
    }

    [Fact]
    public async Task ProactiveReadReplay_StaleSelectedActionNeverStartsAModelOrWrite()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupOfferSelection(tool.Name, "smallStep");
        AstraOfferFact? issued = null;
        _offers.IssueAsync(UserId, Arg.Any<Guid>(), Arg.Any<AstraOfferFact>(), Arg.Any<CancellationToken>())
            .Returns(call => { issued = call.Arg<AstraOfferFact>(); return Task.CompletedTask; });
        var client = OfferClient("en");
        (await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client), default)).IsSuccess.Should().BeTrue();
        issued.Should().NotBeNull();
        _offers.IssuedAsync(UserId, client.ConversationId!.Value, Arg.Any<CancellationToken>()).Returns(new[] { issued! });
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Queries.RefreshReadBlockQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new QueryReadBlock("habitList", new HabitListCard("today", []), issued!.Source! with { Revision = "changed" })));
        _aiIntentService.ClearReceivedCalls();
        _operationExecutor.ClearReceivedCalls();
        var result = await handler.Handle(new(UserId, "Select the offered step.",
            ClientContext: client with { SelectedOfferKey = issued!.Action.OfferKey }), default);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("stale_offer");
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task ProactiveReadReplay_SelectedTargetTravelsOutsideTheVisiblePrompt()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Water", FrequencyUnit.Day, 1, Today,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        SetupOfferHabits(habit);
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupOfferSelection(tool.Name, "log");
        AstraOfferFact? issued = null;
        _offers.IssueAsync(UserId, Arg.Any<Guid>(), Arg.Any<AstraOfferFact>(), Arg.Any<CancellationToken>())
            .Returns(call => { issued = call.Arg<AstraOfferFact>(); return Task.CompletedTask; });
        var client = OfferClient("en");
        var initial = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client), default);
        initial.IsSuccess.Should().BeTrue();
        issued.Should().NotBeNull();
        issued!.Action.Prompt.Should().NotContain(habit.Id.ToString());
        var source = initial.Value.Parts!.OfType<ChatBlockPart>().Single().Block;
        _offers.IssuedAsync(UserId, client.ConversationId!.Value, Arg.Any<CancellationToken>()).Returns(new[] { issued });
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Queries.RefreshReadBlockQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new QueryReadBlock("habitList", source, issued.Source!)));
        _aiIntentService.ClearReceivedCalls();
        var selected = await handler.Handle(new(UserId, "Select this action.",
            ClientContext: client with { SelectedOfferKey = issued.Action.OfferKey }), default);
        selected.IsSuccess.Should().BeTrue(selected.Error);
        var request = _aiIntentService.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "SendWithToolsAsync")
            .Select(call => (AiToolRequest)call.GetArguments()[0]!).First();
        request.UserMessage.Should().Be(issued.Action.Prompt).And.NotContain(habit.Id.ToString());
        request.DynamicContext.Should().Contain("The person selected this server-issued optional action.")
            .And.Contain("\"EntityId\":\"" + habit.Id + "\"");
    }

    private static AgentClientContext OfferClient(string locale) => new(Locale: locale, SupportsParts: true,
        PartKinds: ["habitList", "goalList"], SupportsHabitListCard: true, SupportsGoalListCard: true,
        SupportsDatedHabitListCard: true, SupportsSkippedHabitRows: true, SupportsHabitListDoneStatus: true,
        SupportsPendingOperationChanges: true, SupportsProactiveOffers: true, ConversationId: Guid.NewGuid());

    private void SetupOfferHabits(params Habit[] habits)
    {
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToArray());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToArray());
    }

    private void SetupOfferSelection(string tool, string family, string mode = "valid", string? secondTool = null)
    {
        _offers.IssuedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AstraOfferFact>());

        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AiToolRequest>();
                if (request.SystemPrompt != ProactiveOfferPlanner.SelectionInstruction)
                    return Result.Success(ToolResponse(tool, "read", """{"date":"today","orbit_read_complete":true}""") with
                    {
                        ToolCalls = secondTool is null ? [new(tool, "read", ParseArguments("""{"date":"today","orbit_read_complete":true}"""))]
                            : [new(tool, "read", ParseArguments("""{"date":"today","orbit_read_complete":true}""")),
                               new(secondTool, "second", ParseArguments("""{"orbit_read_complete":true}"""))]
                    });
                request.ToolDeclarations.Should().BeEmpty();
                if (mode == "timeout") throw new OperationCanceledException();
                if (mode == "tools") return Result.Success(ToolResponse("create_habit", "forbidden", "{}"));
                if (mode == "malformed") return Result.Success(new AiResponse { TextMessage = "invalid" });
                using var data = JsonDocument.Parse(request.DynamicContext!);
                var id = data.RootElement.GetProperty("Candidates").EnumerateArray().FirstOrDefault(candidate =>
                    candidate.GetProperty("Family").GetString() == family);
                return Result.Success(new AiResponse
                {
                    TextMessage = JsonSerializer.Serialize(new
                    {
                        candidateId = mode == "unknown" ? "invented" : id.ValueKind == JsonValueKind.Undefined ? null : id.GetProperty("Id").GetString(),
                        interpretation = (string?)null,
                        dismissedOfferKeys = Array.Empty<string>()
                    })
                });
            });
    }
}
