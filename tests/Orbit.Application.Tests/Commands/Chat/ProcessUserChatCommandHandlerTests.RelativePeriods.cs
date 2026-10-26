using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("How many habits did I complete this week?", "en", "America/New_York", "2026-10-20", "2026-10-26")]
    [InlineData("Quantos hábitos eu concluí esta semana?", "pt-BR", "America/Sao_Paulo", "2026-10-19", "2026-10-25")]
    public async Task RelativeWeek_ProductionReadUsesCurrentMondayDespiteModelDates(
        string message, string language, string zone, string modelFrom, string modelTo)
    {
        SetupUserAndPayGate();
        var monday = new DateOnly(2026, 10, 26);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(monday);
        _userDateService.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.FindSystemTimeZoneById(zone));
        var user = (await _userRepo.GetByIdAsync(UserId, default))!;
        user.SetLanguage(language);
        user.SetTimeZone(zone);
        var current = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Current", FrequencyUnit.Day, 1, monday)).Value;
        var previous = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Previous", FrequencyUnit.Day, 1, monday.AddDays(-7))).Value;
        current.Log(monday, advanceDueDate: false);
        previous.Log(monday.AddDays(-1), advanceDueDate: false);
        previous.Log(monday.AddDays(-7), advanceDueDate: false);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { current, previous });
        var queryHandler = new GetPeriodAnalyticsQueryHandler(_habitRepo, _userRepo);
        _mediator.Send(Arg.Any<GetPeriodAnalyticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetPeriodAnalyticsQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetPeriodAnalyticsTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "week", JsonSerializer.Serialize(new
        {
            relative_period = "this_week",
            date_from = modelFrom,
            date_to = modelTo,
            metric = "completedHabits"
        })));
        ContinueWithReadDirective("[[orbit:read:week]]");

        var result = await handler.Handle(new(UserId, message,
            ClientContext: new(SupportsParts: true, PartKinds: ["periodTotal"], SupportsCompletedHabitsMetric: true)), default);

        result.IsSuccess.Should().BeTrue();
        var part = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        var card = part.Block.Should().BeOfType<PeriodTotalCard>().Subject;
        card.Metrics.Window.From.Should().Be(monday);
        card.Metrics.Window.To.Should().Be(monday);
        card.Value.Should().Be(1);
        card.Read!.Scope.GetProperty("date_from").GetString().Should().Be("2026-10-26");
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Is<AiToolRequest>(request =>
                request.DynamicContext!.Contains("this week / esta semana to this_week")
                && request.DynamicContext.Contains("last week / semana passada to last_week")
                && request.DynamicContext.Contains("baseline_relative_period")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
