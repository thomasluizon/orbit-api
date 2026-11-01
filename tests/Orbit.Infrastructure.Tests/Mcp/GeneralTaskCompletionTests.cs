using System.Linq.Expressions;
using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Mcp;

public class GeneralTaskCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetHabit_ReportsPermanentCompletionAndReopening(bool repeat)
    {
        var today = new DateOnly(2026, 4, 3);
        var userId = Guid.NewGuid();
        var task = Habit.Create(new HabitCreateParams(HabitSchedule.General, userId, "Undated task", null, null,
            DueDate: today.AddDays(-1), RepeatReminder: repeat ? new Orbit.Domain.ValueObjects.RepeatReminderOptions(480) : null)).Value;
        task.Log(today.AddDays(-1)).IsSuccess.Should().BeTrue();
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { task }.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        var logs = Substitute.For<IGenericRepository<HabitLog>>();
        logs.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => task.Logs.Where(call.ArgAt<Expression<Func<HabitLog, bool>>>(0).Compile()).ToList());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var handler = new GetHabitByIdQueryHandler(habits, logs, dates, Substitute.For<IRepeatReminderReader>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<GetHabitByIdQuery>(), call.Arg<CancellationToken>()));
        var tools = new HabitTools(mediator, dates, new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>()));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));

        var completed = await tools.GetHabit(user, task.Id.ToString());

        completed.Should().Contain("Status: Completed").And.Contain("Type: One-time task").And.Contain("General: Yes");
        task.Unlog(today.AddDays(-1)).IsSuccess.Should().BeTrue();
        (await tools.GetHabit(user, task.Id.ToString())).Should().Contain("Status: Active");
    }
}
