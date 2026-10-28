using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MetricsReplay_CompletionSeriesMustRepresentTheReportedCompletions(bool backdated)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Water",
            FrequencyUnit.Day, 1, Today.AddDays(-6),
            CreatedAtUtc: (backdated ? Today : Today.AddDays(-6)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        foreach (var offset in new[] { -6, -4, -3, -2, -1 })
            habit.Log(Today.AddDays(offset)).IsSuccess.Should().BeTrue();
        habit.Update(new(HabitSchedule.Recurring, habit.Title, null, FrequencyUnit.Day, 1, [], false, Today))
            .IsSuccess.Should().BeTrue();
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habit);
        var metricsHandler = new GetHabitMetricsQueryHandler(_habitRepo, _userRepo, _userDateService);
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => metricsHandler.Handle(call.Arg<GetHabitMetricsQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetHabitMetricsTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "metrics", JsonSerializer.Serialize(new
        {
            habit_id = habit.Id,
            orbit_read_complete = true
        })));

        var result = await handler.Handle(new(UserId, "Show the requested metrics.",
            ClientContext: new(SupportsParts: true, PartKinds: ["metrics"], SupportsMetricsCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var card = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<MetricsCard>().Subject;
        card.TotalCompletions.Should().Be(5);
        card.PeriodMetrics!.PositiveLogRecords.Should().Be(5);
        card.PeriodMetrics.DistinctLoggedDates.Should().Be(5);
        if (backdated)
        {
            card.PeriodMetrics.MetScheduledDates.Should().Be(0);
            card.PeriodMetrics.ScheduledDates.Should().Be(1);
            card.Series.Should().BeNull("a scheduled-completion chart cannot represent these backdated log records");
        }
        else
        {
            card.Series.Should().NotBeNull();
            card.Series!.Points.Sum(point => point.Completed).Should().Be(card.PeriodMetrics.PositiveLogRecords);
        }
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }
}
