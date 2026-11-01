using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Mcp;

public class GeneralTaskCompletionTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 45)]
    [InlineData(true, 400)]
    public async Task GetHabit_ReportsPermanentCompletionAndReopening(bool repeat, int daysEarlier)
    {
        var today = new DateOnly(2026, 4, 3);
        var userId = Guid.NewGuid();
        var task = Habit.Create(new HabitCreateParams(HabitSchedule.General, userId, "Undated task", null, null,
            DueDate: today.AddDays(-daysEarlier), RepeatReminder: repeat ? new Orbit.Domain.ValueObjects.RepeatReminderOptions(480) : null)).Value;
        task.Log(today.AddDays(-daysEarlier)).IsSuccess.Should().BeTrue();
        var logId = task.Logs.Single().Id;
        var options = new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var seed = new OrbitDbContext(options))
        {
            seed.Habits.Add(task);
            await seed.SaveChangesAsync();
        }
        await using var context = new OrbitDbContext(options);
        var habits = new GenericRepository<Habit>(context);
        var logs = new GenericRepository<HabitLog>(context);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var handler = new GetHabitByIdQueryHandler(habits, logs, dates, Substitute.For<IRepeatReminderReader>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<GetHabitByIdQuery>(), call.Arg<CancellationToken>()));
        await using var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(userId, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var unlog = new UnlogHabitTool(habits, dates, work, payGate, Substitute.For<IUserStreakService>(), Substitute.For<IGoalCompletionService>());
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            request.OperationId.Should().Be(unlog.Name);
            request.Surface.Should().Be(AgentExecutionSurface.Mcp);
            request.Arguments.TryGetProperty("date", out _).Should().BeFalse();
            var result = await unlog.ExecuteAsync(request.Arguments, request.UserId, call.Arg<CancellationToken>());
            result.Success.Should().BeTrue(result.Error);
            return new AgentExecuteOperationResponse(new AgentOperationResult(request.OperationId, unlog.Name,
                AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded,
                TargetId: result.EntityId, TargetName: result.EntityName));
        });
        var tools = new HabitTools(mediator, dates, new McpExecutorBridge(executor));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));

        var completed = await tools.GetHabit(user, task.Id.ToString());

        completed.Should().Contain("Status: Completed").And.Contain("Type: One-time task").And.Contain("General: Yes");
        (await tools.UnlogHabit(user, task.Id.ToString())).Should().Contain("Unlogged habit");
        (await tools.GetHabit(user, task.Id.ToString())).Should().Contain("Status: Active");
        await using var verify = new OrbitDbContext(options);
        (await verify.Habits.SingleAsync()).IsCompleted.Should().BeFalse();
        var row = (await verify.HabitLogs.IgnoreQueryFilters().ToListAsync()).Should().ContainSingle().Subject;
        row.Id.Should().Be(logId);
        row.IsDeleted.Should().BeTrue();
    }
}
