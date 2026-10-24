using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MediatR;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BlockAnswerWriteRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_TransientTransactionFailure_ExecutesToolAndListsActionOnce(bool commitFailure)
    {
        var failure = new TransientTransactionFailure(commitFailure);
        using var factory = new SqliteOrbitDbContextFactory(
            dependencies => new NpgsqlRetryingExecutionStrategy(dependencies, 1, TimeSpan.Zero, null), failure);
        var db = factory.Context;
        var user = User.Create("Alex", "retry@example.com").Value;
        db.Users.Add(user);
        var block = IssuedAstraBlock.Create(user.Id, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], []));
        db.IssuedAstraBlocks.Add(block);
        await db.SaveChangesAsync();
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var executor = Substitute.For<IAgentOperationExecutor>();
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns("create_habit");
        tool.GetParameterSchema().Returns(((IAiTool)RuntimeHelpers.GetUninitializedObject(typeof(CreateHabitTool))).GetParameterSchema());
        tool.ExecuteAsync(Arg.Any<JsonElement>(), user.Id, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Walk", FrequencyUnit.Day, 1,
                new DateOnly(2026, 4, 3))).Value;
            db.Habits.Add(habit);
            await work.SaveChangesAsync(call.Arg<CancellationToken>());
            return new ToolResult(true, habit.Id.ToString(), habit.Title);
        });
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var result = await tool.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>().Arguments, user.Id, call.Arg<CancellationToken>());
            return new AgentExecuteOperationResponse(new("create_habit", "create_habit", AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, TargetId: result.EntityId, TargetName: result.EntityName));
        });
        var catalog = Substitute.For<IAgentCatalogService>();
        catalog.GetCapabilityByChatTool("create_habit").Returns(new AgentCapability("create_habit", "Create", "Create", "habits", "habits:write",
            AgentRiskClass.Low, true, false, AgentConfirmationRequirement.None));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(user.Id, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var ai = Substitute.For<IAiIntentService>();
        ai.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                failure.Enabled = true;
                return Result.Success(new AiResponse { ToolCalls = [new AiToolCall("create_habit", "write", JsonSerializer.SerializeToElement(new { title = "Walk" }))] });
            });
        ai.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<AiResponse>(ErrorMessages.AiUnavailable));
        var flags = Substitute.For<IFeatureFlagService>();
        var analytics = Substitute.For<IProductAnalytics>();
        var pending = Substitute.For<IPendingAgentOperationStore>();
        var handler = new ProcessUserChatCommandHandler(
            new(new GenericRepository<Habit>(db), new GenericRepository<Goal>(db), new GenericRepository<User>(db),
                new GenericRepository<Tag>(db), new GenericRepository<ChecklistTemplate>(db), flags),
            new(ai, new AiToolRegistry([tool]), Substitute.For<ISystemPromptBuilder>(), catalog),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, Substitute.For<IServiceScopeFactory>(), executor,
                Substitute.For<IPendingClarificationStore>(), Substitute.For<IGoalProgressReadSyncer>(), Substitute.For<IGamificationService>(),
                Substitute.For<IMediator>(), analytics, Substitute.For<IHabitScheduleLogReader>(), new IssuedAstraBlockStore(db), pending),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), Orbit.Infrastructure.Tests.ModerationTestDouble.Clean());
        var request = new ProcessUserChatCommand(user.Id, "Evening", BlockAnswer: new(block.Id, ["b"]));

        var first = await handler.Handle(request, default);
        var replay = await handler.Handle(request, default);

        first.IsSuccess.Should().BeTrue();
        first.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Success);
        replay.Value.Should().BeEquivalentTo(first.Value);
        failure.Failures.Should().Be(1);
        failure.Attempts.Should().Be(2);
        await tool.Received(1).ExecuteAsync(Arg.Any<JsonElement>(), user.Id, Arg.Any<CancellationToken>());
        (await db.Habits.CountAsync(habit => habit.UserId == user.Id)).Should().Be(1);
    }

    private sealed class TransientTransactionFailure(bool commitFailure) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public int Attempts { get; private set; }
        public int Failures { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !commitFailure)
                FailFirstAttempt();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && commitFailure)
                FailFirstAttempt();
            return Task.CompletedTask;
        }

        private void FailFirstAttempt()
        {
            Attempts++;
            if (Failures != 0)
            {
                Enabled = false;
                return;
            }
            Failures++;
            throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
        }
    }
}
