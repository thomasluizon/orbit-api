using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class CreateHabitToolPersistenceTests
{
    private static readonly DateOnly Today = new(2026, 8, 6);

    [Theory]
    [InlineData("""{"title":"Varrer a garagem","frequency_unit":"Week","frequency_quantity":1,"days":["Thursday"],"due_time":null}""", DayOfWeek.Thursday, null)]
    [InlineData("""{"title":"Varrer a garagem","frequency_unit":"Week","frequency_quantity":1,"days":["Tuesday"],"due_time":"15:00"}""", DayOfWeek.Tuesday, 15)]
    [InlineData("""{"title":"Varrer a garagem","description":"Varrer a garagem às terças-feiras antes das 15h, sem horário fixo","emoji":"🧹","frequency_unit":"Week","frequency_quantity":1,"days":["Tuesday"],"due_time":"15:00"}""", DayOfWeek.Tuesday, 15)]
    public async Task RecordedAuditPayload_PersistsRequestedWeekday(string arguments, DayOfWeek weekday, int? startHour)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = await SeedUserAsync(db);
        var handler = CreateHandler(db, user, arguments);

        var result = await handler.Handle(new(user.Id,
            "astra crie um habito de varrer a garagem às 5as, sem horário definido mas antes das 15h, marque o de hoje como concluído"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Succeeded,
            "operation result was {0}", JsonSerializer.Serialize(result.Value.Operations));
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Success);
        db.ChangeTracker.Clear();
        var habit = (await db.Habits.AsNoTracking().ToListAsync()).Should().ContainSingle().Subject;
        habit.UserId.Should().Be(user.Id);
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        habit.FrequencyQuantity.Should().Be(1);
        habit.Days.Should().Equal(weekday);
        habit.DueTime.Should().Be(startHour is { } hour ? new TimeOnly(hour, 0) : null);
        HabitScheduleService.GetScheduledDates(habit, Today, Today.AddDays(20), weekStartDay: 1)
            .Should().OnlyContain(date => date.DayOfWeek == weekday).And.HaveCount(3);
    }

    [Theory]
    [InlineData("Tenho aula toda quarta das 8h25 às 11h15, e no dia 26/08 tenho uma aula extra das 11h15 às 12h05.")]
    [InlineData("Class every Wednesday from 08:25 to 11:15, plus an extra class on 26/08 from 11:15 to 12:05.")]
    public async Task TicketMixedRequest_PersistsSeparateRecurringAndOneTimeClasses(string message)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = await SeedUserAsync(db);
        var audits = new List<AgentAuditEntry>();
        var handler = CreateHandler(db, user, audits,
            """{"title":"Aula semanal","frequency_unit":"Week","frequency_quantity":1,"days":["Wednesday"],"due_time":"08:25","due_end_time":"11:15"}""",
            """{"title":"Aula extra","frequency_unit":null,"due_date":"2026-08-26","due_time":"11:15","due_end_time":"12:05"}""");

        var result = await handler.Handle(new(user.Id, message), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded,
            "operations were {0}; audited arguments were {1}", JsonSerializer.Serialize(result.Value.Operations), JsonSerializer.Serialize(audits));
        result.Value.Operations.Should().OnlyContain(operation => operation.OperationId == "create_habit");
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Success);
        db.ChangeTracker.Clear();
        var habits = await db.Habits.AsNoTracking().ToListAsync();
        habits.Should().HaveCount(2).And.OnlyContain(habit => habit.ParentHabitId == null && habit.UserId == user.Id);
        var recurring = habits.Single(habit => habit.Title == "Aula semanal");
        recurring.Schedule.Should().Be(HabitSchedule.Recurring);
        recurring.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        recurring.FrequencyQuantity.Should().Be(1);
        recurring.Days.Should().Equal(DayOfWeek.Wednesday);
        recurring.DueTime.Should().Be(new TimeOnly(8, 25));
        recurring.DueEndTime.Should().Be(new TimeOnly(11, 15));
        var dated = habits.Single(habit => habit.Title == "Aula extra");
        dated.Schedule.Should().Be(HabitSchedule.OneTime);
        dated.FrequencyUnit.Should().BeNull();
        dated.FrequencyQuantity.Should().BeNull();
        dated.IntervalWeeks.Should().BeNull();
        dated.Days.Should().BeEmpty();
        dated.NextDueDate.Should().Be(new DateOnly(2026, 8, 26));
        dated.DueTime.Should().Be(new TimeOnly(11, 15));
        dated.DueEndTime.Should().Be(new TimeOnly(12, 5));
    }

    private static async Task<User> SeedUserAsync(OrbitDbContext db)
    {
        var user = User.Create("Alex", "classes@example.test").Value;
        user.SetLanguage("pt-BR");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ProcessUserChatCommandHandler CreateHandler(OrbitDbContext db, User user, params string[] arguments)
        => CreateHandler(db, user, [], arguments);

    private static ProcessUserChatCommandHandler CreateHandler(OrbitDbContext db, User user, List<AgentAuditEntry> audits, params string[] arguments)
    {
        var habits = new GenericRepository<Habit>(db);
        var tags = new GenericRepository<Tag>(db);
        var goals = new GenericRepository<Goal>(db);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(user.Id, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        quota.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tool = new CreateHabitTool(habits, tags, goals, dates, quota, work);
        var registry = new AiToolRegistry([tool]);
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
            new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed,
                catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), user.Id, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var pending = Substitute.For<IPendingAgentOperationStore>();
        var audit = Substitute.For<IAgentAuditService>();
        audit.RecordAsync(Arg.Do<AgentAuditEntry>(audits.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var executor = new AgentOperationExecutor(catalog, policy, audit, ownership,
            bridge, registry, Substitute.For<IPendingOperationChangePreviewer>(), pending, work,
            NullLogger<AgentOperationExecutor>.Instance);
        var ai = Substitute.For<IAiIntentService>();
        ai.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = arguments.Select((json, index) => new AiToolCall("create_habit", $"create_{index}",
                    JsonSerializer.Deserialize<JsonElement>(json))).ToList(),
                ConversationContext = new AiConversationContext { Messages = new List<object>(), Options = new object() }
            }));
        ai.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Salvei." }));
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        var syncer = Substitute.For<IGoalProgressReadSyncer>();
        syncer.ComputeFreshValuesAsync(user.Id, Today, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var logs = Substitute.For<IHabitScheduleLogReader>();
        logs.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(Array.Empty<HabitScheduleLogDay>());
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(false, false, []));

        return new ProcessUserChatCommandHandler(
            new(habits, goals, new GenericRepository<User>(db), tags, new GenericRepository<ChecklistTemplate>(db), flags),
            new(ai, registry, Substitute.For<ISystemPromptBuilder>(), catalog),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, Substitute.For<IServiceScopeFactory>(), executor,
                Substitute.For<IPendingClarificationStore>(), syncer, Substitute.For<IGamificationService>(),
                Substitute.For<IMediator>(), Substitute.For<IProductAnalytics>(), logs, new IssuedAstraBlockStore(db), pending),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), moderation);
    }
}
