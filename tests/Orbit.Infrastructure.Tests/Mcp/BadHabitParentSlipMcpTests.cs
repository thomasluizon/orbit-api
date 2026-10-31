using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Mcp;

public sealed class BadHabitParentSlipMcpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mcp_LogAndUnlog_DerivesParentThroughSharedTools(bool bulk)
    {
        var userId = Guid.NewGuid();
        var today = new DateOnly(2026, 10, 20);
        await using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var parent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId,
            "Parent", FrequencyUnit.Day, 1, today, IsBadHabit: true)).Value;
        var first = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId,
            "First child", FrequencyUnit.Day, 1, today, IsBadHabit: true, ParentHabitId: parent.Id)).Value;
        var second = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId,
            "Second child", FrequencyUnit.Day, 1, today, IsBadHabit: true, ParentHabitId: parent.Id)).Value;
        context.Habits.AddRange(parent, first, second);
        await context.SaveChangesAsync();
        var habits = new GenericRepository<Habit>(context);
        var logs = new GenericRepository<HabitLog>(context);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var streaks = Substitute.For<IUserStreakService>();
        var gamification = Substitute.For<IGamificationService>();
        var payGate = Substitute.For<IPayGateService>();
        var goals = Substitute.For<IGoalCompletionService>();
        goals.SyncDerivedGoalsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await context.SaveChangesAsync(call.Arg<CancellationToken>());
            return (IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>();
        });
        var mediator = Substitute.For<IMediator>();
        var singleHandler = new LogHabitCommandHandler(new LogHabitRepositories(habits, logs,
                new GenericRepository<User>(context), new GenericRepository<SentReminder>(context)),
            new LogHabitServices(dates, streaks, gamification, goals, mediator, payGate), work, cache,
            NullLogger<LogHabitCommandHandler>.Instance);
        var bulkHandler = new BulkLogHabitsCommandHandler(habits, logs,
            new BulkLogServices(dates, streaks, gamification), goals, work, cache,
            NullLogger<BulkLogHabitsCommandHandler>.Instance);
        mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => singleHandler.Handle(call.Arg<LogHabitCommand>(), call.Arg<CancellationToken>()));
        mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => bulkHandler.Handle(call.Arg<BulkLogHabitsCommand>(), call.Arg<CancellationToken>()));
        IAiTool[] tools =
        [
            new LogHabitTool(mediator, habits, dates),
            new BulkLogHabitsTool(mediator, habits, dates, new BulkHabitReplayPlanner(
                Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), work)),
            new UnlogHabitTool(habits, dates, work, payGate, streaks, goals)
        ];
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            request.Surface.Should().Be(AgentExecutionSurface.Mcp);
            var tool = tools.Single(tool => tool.Name == request.OperationId);
            var result = await tool.ExecuteAsync(request.Arguments, request.UserId, call.Arg<CancellationToken>());
            result.Success.Should().BeTrue(result.Error);
            return new AgentExecuteOperationResponse(new AgentOperationResult(request.OperationId, tool.Name,
                AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded,
                TargetId: result.EntityId, TargetName: result.EntityName, Payload: result.Payload));
        });
        var mcp = new HabitTools(mediator, dates, new McpExecutorBridge(executor));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));

        if (bulk)
            await mcp.BulkLogHabits(user, $"{first.Id},{second.Id}");
        else
        {
            await mcp.LogHabit(user, first.Id.ToString());
            (await context.HabitLogs.Where(log => log.HabitId == parent.Id).ToListAsync()).Should().BeEmpty();
            await mcp.LogHabit(user, second.Id.ToString());
        }
        (await context.HabitLogs.Where(log => log.HabitId == parent.Id).ToListAsync())
            .Should().ContainSingle().Which.IsSlip.Should().BeTrue();
        await mcp.BulkLogHabits(user, $"{first.Id},{second.Id}");
        (await context.HabitLogs.Where(log => log.HabitId == parent.Id).ToListAsync()).Should().ContainSingle();

        await mcp.UnlogHabit(user, first.Id.ToString());

        context.ChangeTracker.Clear();
        (await context.HabitLogs.Where(log => log.HabitId == parent.Id || log.HabitId == first.Id).ToListAsync())
            .Should().BeEmpty();
        (await context.HabitLogs.Where(log => log.HabitId == second.Id).ToListAsync()).Should().ContainSingle();
    }
}
