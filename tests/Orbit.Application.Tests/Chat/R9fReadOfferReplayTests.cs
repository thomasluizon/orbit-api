using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "due-log-en-r1", "log")]
    [InlineData("pt-BR", "due-log-en-r1", "log")]
    [InlineData("en", "missed-en-r1", "restart")]
    [InlineData("pt-BR", "missed-en-r1", "restart")]
    [InlineData("en", "reminder-gap-en-r1", "reminder")]
    [InlineData("pt-BR", "reminder-gap-en-r1", "reminder")]
    [InlineData("en", "empty-en-r1", "smallStep")]
    [InlineData("pt-BR", "empty-en-r1", "smallStep")]
    [InlineData("en", "goal-gap-en-r2", "aim")]
    [InlineData("pt-BR", "goal-gap-en-r2", "aim")]
    public async Task R9f_TypedFamilyActionUsesDeclaredIntentWithoutASelector(string locale, string capture, string family)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Water", FrequencyUnit.Day, 1,
            family == "log" ? Today : Today.AddDays(-7), ReminderEnabled: family == "restart",
            CreatedAtUtc: Today.AddDays(-14).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        SetupOfferHabits(habit);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new DailySummaryResponse("", "", false,
            new DaySummaryCard(Today, 0, 0, 0, 0, 0))));
        IAiTool tool = family == "smallStep" ? new GetDailySummaryTool(_mediator, _userDateService) : family == "aim" ? new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer)
            : new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var captured = R9fCall(capture, tool.Name);
        if (family == "aim")
        {
            var arguments = JsonNode.Parse(captured.Args.GetRawText())!.AsObject();
            arguments["orbit_offer_intent"] = "aim";
            captured = captured with { Args = JsonSerializer.SerializeToElement(arguments) };
        }
        SetupAiResponse(new AiResponse { ToolCalls = [captured], ConversationContext = TestConversationContext });
        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: OfferClient(locale) with { SupportsDaySummaryCard = true, PartKinds = ["habitList", "goalList", "daySummary"] }), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        var block = result.Value.Parts!.OfType<ChatBlockPart>().Single().Block;
        ReadBlockTelemetry.Describe(block).Read!.NextAction!.OfferKey.Should().StartWith(family + ":");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(),
            Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task R9f_EmptyMonthFallbackStatesNoData(string locale)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new GetHabitMonthTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        _mediator.Send(Arg.Any<GetHabitMonthQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new HabitMonthCard(Today, Today, Today, 1, new([], []))));
        var captured = R9fCall("empty-en-r2", tool.Name);
        SetupAiResponse(new AiResponse { ToolCalls = [captured with { Args = WithCalendarWindow(tool.Name, captured.Args) }], ConversationContext = TestConversationContext });
        var result = await handler.Handle(new(UserId, "Read the requested window.", ClientContext: OfferClient(locale)), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Contain(locale == "en" ? "No habits logged in this period." : "Nenhum hábito registrado neste período.");
        result.Value.AiMessage.Should().NotContain(locale == "en" ? "Logs:" : "Registros:");
    }
}
