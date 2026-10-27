using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ChatContextScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidates_TranslateNarrowProjectionForPostgres(bool placement)
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=translation_only").Options);
        var userId = Guid.NewGuid();

        var sql = ChatHabitCandidate.Select(db.Habits.AsNoTracking().Where(habit => habit.UserId == userId),
            new DateOnly(2026, 4, 3), placement).ToQueryString();

        sql.Should().Contain("EXISTS").And.Contain("\"Title\"").And.Contain("\"UserId\"")
            .And.NotContain("JOIN").And.NotContain("\"Description\"").And.NotContain("\"ReminderTimes\"")
            .And.NotContain("\"Note\"");
        if (!placement) sql.Should().NotContain("h.\"ChecklistItems\"");
        output.WriteLine(sql);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(2000)]
    public async Task RankingTurn_BoundsContextHydrationAndHistory(int habitCount)
    {
        var counter = new CountingDbCommandInterceptor();
        var entities = new HabitMaterializations();
        var encryption = new EncryptionService(Options.Create(new EncryptionSettings
        {
            Key = Convert.ToBase64String(new byte[32])
        }), NullLogger<EncryptionService>.Instance);
        using var factory = new SqliteOrbitDbContextFactory(encryption, counter, entities);
        var db = factory.Context;
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Ranking user", "context@example.test").Value;
        var tag = Tag.Create(user.Id, "Health", "#123456").Value;
        var habits = Enumerable.Range(0, habitCount).Select(i => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, user.Id, $"Private habit {i}", FrequencyUnit.Day, 1,
            DueDate: today.AddDays(-49), Description: new string('x', 2000), Position: i)).Value).ToArray();
        foreach (var habit in habits)
        {
            habit.AddTag(tag);
            for (var day = 0; day < 50; day++)
                habit.Log(today.AddDays(-day), advanceDueDate: false);
        }
        db.Users.Add(user);
        db.Habits.AddRange(habits);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        var users = new GenericRepository<User>(db);
        var repository = new GenericRepository<Habit>(db);
        var jwt = Options.Create(new JwtSettings
        {
            SecretKey = "chat-context-scale-test-signing-key-32-bytes",
            Issuer = "test",
            Audience = "test"
        });
        var ranking = new GetHabitRankingQueryHandler(new HabitRankingReader(db, encryption), dates, new HabitRankingTokenService(jwt),
            Substitute.For<IProductAnalytics>(), NullLogger<GetHabitRankingQueryHandler>.Instance);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => ranking.Handle(call.Arg<GetHabitRankingQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetHabitRankingTool(mediator);
        var blocks = new ReadBlockFactory(dates, new ReadQueryTokenService(jwt), users);
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
            new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed,
                catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, bridge,
            new AiToolRegistry([tool]), Substitute.For<IPendingOperationChangePreviewer>(),
            Substitute.For<IPendingAgentOperationStore>(), Substitute.For<IUnitOfWork>(),
            NullLogger<AgentOperationExecutor>.Instance, blocks);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraEvalTrace });
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(user.Id, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var intent = Substitute.For<IAiIntentService>();
        CapturedDbCommand[] contextCommands = [];
        var contextHabits = 0;
        intent.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(call =>
        {
            contextCommands = counter.Commands.ToArray();
            contextHabits = entities.Habits;
            call.Arg<AiToolRequest>().DynamicContext.Should().Contain("Private habit 0").And.Contain("Health");
            return Result.Success(new AiResponse
            {
                ToolCalls = [new(tool.Name, "ranking", JsonSerializer.SerializeToElement(new
                {
                    date_from = today.AddDays(-29).ToString("yyyy-MM-dd"),
                    date_to_exclusive = today.AddDays(1).ToString("yyyy-MM-dd")
                }))],
                ConversationContext = new AiConversationContext()
            });
        });
        intent.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
            { TextMessage = "Here is your ranking.\n[[orbit:read:ranking]]" }));
        using var services = new ServiceCollection().AddSingleton<IAgentOperationExecutor>(executor)
            .AddSingleton(Substitute.For<IPendingClarificationStore>()).BuildServiceProvider();
        var handler = new ProcessUserChatCommandHandler(
            new(repository, new GenericRepository<Goal>(db), users, new GenericRepository<Tag>(db),
                new GenericRepository<ChecklistTemplate>(db), flags),
            new(intent, new AiToolRegistry([tool]), new SystemPromptBuilder(), catalog),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, services.GetRequiredService<IServiceScopeFactory>(),
                executor, Substitute.For<IPendingClarificationStore>(), Substitute.For<IGoalProgressReadSyncer>(),
                Substitute.For<IGamificationService>(), mediator, Substitute.For<IProductAnalytics>(),
                new HabitScheduleLogReader(db), Substitute.For<IIssuedAstraBlockStore>(),
                Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(),
            Orbit.Infrastructure.Tests.ModerationTestDouble.Clean(), readBlocks: blocks);
        counter.Reset();
        entities.Habits = 0;

        var result = await handler.Handle(new(user.Id,
            "Rank my habits by how many times I completed them in the last 30 days",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitRanking"]), EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        var timing = result.Value.EvalTrace!.Timing!;
        output.WriteLine($"habits={habitCount}; logs={habitCount * 50}; "
            + $"context_load_ms={timing.Phases.Where(p => p.Name == "context_load").Sum(p => p.Milliseconds):F2}; "
            + $"ranking_tool_ms={timing.Phases.Where(p => p.Name == "tool_call").Sum(p => p.Milliseconds):F2}; "
            + $"context_habit_entities={contextHabits}; context_rows={contextCommands.Sum(c => c.Rows)}");
        var block = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        block.Kind.Should().Be("habitRanking");
        var ranked = block.Block.Should().BeOfType<HabitRankingCard>().Subject.Ranking;
        ranked.TotalCount.Should().Be(habitCount);
        ranked.Rows.Should().OnlyContain(row => row.CompletedCount == 30);
        ranked.Query.TimeZone.Should().Be("America/Sao_Paulo");
        contextCommands.Sum(c => c.Rows).Should().BeLessThanOrEqualTo(habitCount + 3 * AppConstants.MaxPromptHabitEntries + 2);
        contextHabits.Should().BeLessThanOrEqualTo(AppConstants.MaxPromptHabitEntries);
        var rankingSql = counter.Commands.Except(contextCommands).Where(c => c.Sql.Contains("GROUP BY", StringComparison.Ordinal)).ToArray();
        rankingSql.Should().ContainSingle();
        rankingSql[0].Rows.Should().Be(ranked.Rows.Count);
        db.ChangeTracker.HasChanges().Should().BeFalse();
    }

    private sealed class HabitMaterializations : IMaterializationInterceptor
    {
        internal int Habits { get; set; }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is Habit) Habits++;
            return entity;
        }
    }
}
