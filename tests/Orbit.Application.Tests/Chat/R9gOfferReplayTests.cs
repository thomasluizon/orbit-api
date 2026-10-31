using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private static AiToolCall R9gCall(string capture)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "ProactiveOffers", "R9g", "offers-" + capture + ".json")));
        var call = file.RootElement.GetProperty("toolCalls")[0];
        return new(call.GetProperty("name").GetString()!, "captured", call.GetProperty("arguments").Clone());
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task R9gOfferReplay_InvalidSuggestionGetsOneSameTargetRepair(string locale, bool conflict)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var call = R9gCall(conflict ? "repeat-pt-BR-r3" : "repeat-en-r1");
        Orbit.Application.Chat.Tools.IAiTool tool = conflict ? new BulkCreateHabitsTool(_mediator) : new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        var original = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        var draft = conflict ? original["habits"]![0]!.AsObject() : original;
        if (conflict) draft["frequency_quantity"] = 1;
        SetupAiResponse(new AiResponse { ToolCalls = [call with { Args = JsonSerializer.SerializeToElement(original) }], ConversationContext = TestConversationContext });
        var repaired = (JsonObject)original.DeepClone();
        var item = conflict ? repaired["habits"]![0]!.AsObject() : repaired;
        item["frequency_quantity"] = 1;
        if (conflict)
        {
            item.Remove("scheduled_reminders");
            item["suggested_fields"] = new JsonArray("frequency_quantity", "repeat_reminder");
        }
        else item["repeat_reminder"] = new JsonObject { ["interval_minutes"] = 120 };
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>()).Returns(input =>
            {
                var failure = input.Arg<IReadOnlyList<AiToolCallResult>>().Single();
                failure.Success.Should().BeFalse();
                failure.Error.Should().Contain(conflict ? "repeat_reminder" : "frequency_quantity");
                return Result.Success(new AiResponse { ToolCalls = [call with { Id = "repair", Args = JsonSerializer.SerializeToElement(repaired) }], ConversationContext = TestConversationContext });
            });

        var result = await handler.Handle(new(UserId, "Prepare the requested draft.", ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Select(outcome => outcome.Operation.Status).Should().Equal(AgentOperationStatus.Failed, AgentOperationStatus.PendingConfirmation);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items![0].Fields.Should()
            .Contain(field => field.Field == "repeat_reminder" && field.Provenance == "suggested" && field.Removal != null);
        result.Value.PolicyDenials.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
    }

    [Fact]
    public async Task R9gOfferReplay_DayCallCannotUseTheHabitCalendarTool()
    {
        var call = R9gCall("empty-pt-BR-r3");
        var result = await new GetHabitMonthTool(_mediator).ExecuteAsync(call.Args, UserId, default);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HABIT_CALENDAR_RANGE_INVALID");
        _mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task R9gOfferReplay_FirstRoundIncludesOwnedGoalAndRoutineFacts(string locale)
    {
        SetupUserAndPayGate();
        var habit = Habit.Create(new(HabitSchedule.Recurring, UserId, "Routine", FrequencyUnit.Day, 1, Today)).Value;
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Mobility", 30, "sessions", Description: "Move comfortably")).Value;
        SetupOfferHabits(habit);
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        SetupAiResponse(new AiResponse { TextMessage = "Ready." });

        var result = await CreateHandler().Handle(new(UserId, "Prepare the requested draft.", ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = (AiToolRequest)_aiIntentService.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "SendWithToolsAsync").GetArguments()[0]!;
        var snapshot = request.DynamicContext!.Split(Environment.NewLine).Single(line => line.StartsWith("{\"offer_account_facts\":", StringComparison.Ordinal));
        using var facts = JsonDocument.Parse(snapshot);
        var account = facts.RootElement.GetProperty("offer_account_facts");
        account.GetProperty("Habits")[0].GetProperty("Id").GetGuid().Should().Be(habit.Id);
        account.GetProperty("ActiveGoals")[0].GetProperty("Id").GetGuid().Should().Be(goal.Id);
        account.GetProperty("ActiveGoals")[0].GetProperty("Description").GetString().Should().Be(goal.Description);
        account.GetProperty("HabitIndexComplete").GetBoolean().Should().BeTrue();
        request.DynamicContext.Should().Contain("When the message supplies a habit ID, pass it directly to the write tool");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task R9gOfferReplay_TruncatedReadKeepsNoticeAndNeverOffers(string locale)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query);
        UseProductionReadExecutor(query);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AiToolRequest>();
                if (request.SystemPrompt != ProactiveOfferPlanner.SelectionInstruction)
                    return Result.Success(ToolResponse(query.Name, "read", """{"relative_period":"today","orbit_read_complete":false}"""));
                using var data = JsonDocument.Parse(request.DynamicContext!);
                var candidate = data.RootElement.GetProperty("Candidates").EnumerateArray()
                    .First(item => item.GetProperty("Family").GetString() == "smallStep");
                return Result.Success(new AiResponse { TextMessage = JsonSerializer.Serialize(new { candidateId = candidate.GetProperty("Id").GetString() }) });
            });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { IsTruncated = true }));

        var result = await handler.Handle(new(UserId, "Prepare the requested draft.", ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts!.OfType<ChatTextPart>().Should().Contain(part => part.Text == (locale == "en"
            ? "This response was cut off before completion, so the result is partial."
            : "Esta resposta foi interrompida antes do fim e pode estar incompleta."));
        result.Value.Parts!.OfType<ChatBlockPart>().Should().OnlyContain(block => ReadBlockTelemetry.Describe(block.Block).Read!.NextAction == null);
        await _offers.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default!, default);
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task R9gOfferReplay_EmptyDayBlockOwnsItsEmptyState(string locale, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var narrative = locale == "en" ? "There is nothing to pick from today." : "Não há nada para escolher hoje.";
        var card = new DaySummaryCard(Today, 0, 0, null, 0, 0, Rows: [], TotalRows: 0);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryResponse(narrative, "", false, card, "empty")));
        var tool = new GetDailySummaryTool(_mediator, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "day", """{"generate_narrative":true,"orbit_read_complete":true}"""));
        ContinueWithReadDirective(narrative + "\n[[orbit:read:day]]");
        var client = OfferClient(locale) with { SupportsProactiveOffers = false, PartKinds = ["daySummary"], SupportsDaySummaryCard = true };

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("daySummary");
        result.Value.AiMessage.Should().BeNullOrEmpty();
    }
}
