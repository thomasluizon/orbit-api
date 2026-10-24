using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class UpdateHabitToolConcurrencyTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    public static IEnumerable<object[]> Interleavings =>
        from surface in new[] { "chat", "mcp", "bulk" }
        from interleaving in new[] { "before_handler", "pretracked", "retry" }
        from dueTime in new[] { "09:10", "09:30" }
        select new object[] { surface, interleaving, dueTime };

    [Theory]
    [MemberData(nameof(Interleavings))]
    public async Task DueTimeUpdate_ResolvesAgainstCommittedDeadline(string surface, string interleaving, string dueTime)
    {
        var transactions = new InterleavedTransactions();
        using var factory = new SqliteOrbitDbContextFactory(
            dependencies => new NpgsqlRetryingExecutionStrategy(dependencies, 1, TimeSpan.Zero, null), transactions);
        var seed = factory.Context;
        var user = User.Create("Alex", "habit-update@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read", FrequencyUnit.Day, 1, Today,
            DueTime: new TimeOnly(9, 0), DueEndTime: new TimeOnly(10, 0), ReminderEnabled: true,
            ReminderTimes: [15])).Value;
        seed.Users.Add(user);
        seed.Habits.Add(habit);
        seed.SentReminders.AddRange(SentReminder.Create(habit.Id, Today, 15),
            SentReminder.Create(habit.Id, Today.AddDays(-1), 15));
        await seed.SaveChangesAsync();
        await using var first = factory.CreateContext();
        await using var second = factory.CreateContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheKey = $"goal-review:{user.Id}:en";
        cache.Set(cacheKey, "stale");
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        var payGate = Substitute.For<IPayGateService>();
        var firstWork = new UnitOfWork(first, new DatabaseConnectionSettings());
        var firstRepository = new GenericRepository<Habit>(first);
        var firstHandler = CreateHandler(first, firstWork, dates, payGate, cache);
        using var secondCache = new MemoryCache(new MemoryCacheOptions());
        var secondHandler = CreateHandler(second, new UnitOfWork(second, new DatabaseConnectionSettings()),
            dates, payGate, secondCache);
        var writes = 0;
        async Task CommitDeadlineAsync()
        {
            writes++;
            var result = await secondHandler.Handle(new UpdateHabitCommand(user.Id, habit.Id, "Read updated",
                null, FrequencyUnit.Day, 1, DueDate: Today,
                Options: new(DueTime: new TimeOnly(9, 0), DueEndTime: new TimeOnly(9, 15))), default);
            result.IsSuccess.Should().BeTrue();
        }
        var mediator = Substitute.For<IMediator>();
        var pauseBeforeHandler = interleaving != "retry";
        async Task BeforeHandlerAsync()
        {
            if (!pauseBeforeHandler) return;
            pauseBeforeHandler = false;
            await CommitDeadlineAsync();
        }
        mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await BeforeHandlerAsync();
            return await firstHandler.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>());
        });
        var bulkHandler = new BulkUpdateHabitsCommandHandler(firstRepository, dates, payGate, firstWork, cache,
            NullLogger<BulkUpdateHabitsCommandHandler>.Instance);
        mediator.Send(Arg.Any<BulkUpdateHabitsCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await BeforeHandlerAsync();
            return await bulkHandler.Handle(call.Arg<BulkUpdateHabitsCommand>(), call.Arg<CancellationToken>());
        });
        if (interleaving == "pretracked")
            await firstRepository.GetByIdAsync(habit.Id);
        if (interleaving == "retry")
        {
            transactions.Target = first;
            transactions.BeforeRetry = CommitDeadlineAsync;
        }
        var tool = new UpdateHabitTool(firstRepository, dates, mediator);
        ToolResult outcome;
        if (surface == "bulk")
        {
            outcome = await new BulkUpdateHabitsTool(mediator).ExecuteAsync(JsonSerializer.SerializeToElement(new
            {
                filter = new { habit_ids = new[] { habit.Id } },
                updates = new { due_time = dueTime }
            }), user.Id, default);
        }
        else if (surface == "mcp")
        {
            ToolResult? executed = null;
            var executor = Substitute.For<IAgentOperationExecutor>();
            executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                executed = await tool.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>().Arguments, user.Id,
                    call.Arg<CancellationToken>());
                return new AgentExecuteOperationResponse(new("update_habit", "update_habit", AgentRiskClass.Low,
                    AgentConfirmationRequirement.None,
                    executed.Success ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed));
            });
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));
            await new HabitTools(mediator, dates, new McpExecutorBridge(executor))
                .UpdateHabit(principal, habit.Id.ToString(), dueTime: dueTime);
            outcome = executed ?? throw new InvalidOperationException("MCP did not execute update_habit.");
        }
        else
        {
            outcome = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { habit_id = habit.Id, due_time = dueTime }),
                user.Id, default);
        }
        writes.Should().Be(1);
        if (interleaving == "retry") transactions.Failures.Should().Be(1);
        await using var persisted = factory.CreateContext();
        var actual = await persisted.Habits.SingleAsync(h => h.Id == habit.Id);
        actual.DueEndTime.Should().Be(new TimeOnly(9, 15));
        (actual.DueTime < actual.DueEndTime).Should().BeTrue("a deadline must remain after the start");
        actual.Title.Should().Be("Read updated");
        outcome.Success.Should().Be(dueTime == "09:10");
        actual.DueTime.Should().Be(dueTime == "09:10" ? new TimeOnly(9, 10) : new TimeOnly(9, 0));
        if (outcome.Success)
        {
            var reminders = await persisted.SentReminders.Where(r => r.HabitId == habit.Id).ToListAsync();
            if (surface != "bulk") reminders.Should().ContainSingle(r => r.Date == Today.AddDays(-1));
            cache.TryGetValue(cacheKey, out _).Should().BeFalse();
        }
    }

    private static UpdateHabitCommandHandler CreateHandler(OrbitDbContext db, UnitOfWork work,
        IUserDateService dates, IPayGateService payGate, IMemoryCache cache) =>
        new(new GenericRepository<Habit>(db), new GenericRepository<SentReminder>(db), new GenericRepository<Goal>(db),
            payGate, Substitute.For<IGoalCompletionService>(), dates, work, cache,
            new GenericRepository<Tag>(db), Substitute.For<IAppConfigService>());

    private sealed class InterleavedTransactions : DbTransactionInterceptor
    {
        public OrbitDbContext? Target { get; set; }
        public Func<Task>? BeforeRetry { get; set; }
        public int Failures { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(eventData.Context, Target) && Failures == 0)
            {
                Failures++;
                throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(eventData.Context, Target) && Failures == 1 && BeforeRetry is { } beforeRetry)
            {
                BeforeRetry = null;
                await beforeRetry();
            }
            return result;
        }
    }
}
