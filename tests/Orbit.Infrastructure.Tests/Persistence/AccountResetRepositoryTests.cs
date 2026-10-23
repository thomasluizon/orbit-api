using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AccountResetRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly AccountResetRepository _repository;
    private readonly Orbit.Domain.Events.IAccountEventCollector _eventCollector = NSubstitute.Substitute.For<Orbit.Domain.Events.IAccountEventCollector>();
    private readonly AgentCatalogService _catalogService = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();

    public AccountResetRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();
        _dbContext.Database.ExecuteSqlRaw("""
            CREATE TRIGGER AdvanceUserVersion AFTER UPDATE ON "Users"
            WHEN NEW."xmin" = OLD."xmin"
            BEGIN
                UPDATE "Users" SET "xmin" = OLD."xmin" + 1 WHERE "Id" = NEW."Id";
            END;
            """);
        _repository = new AccountResetRepository(_dbContext, _eventCollector);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedForUser(Guid userId)
    {
        _dbContext.ConversationTurns.Add(ConversationTurn.Create(userId, Guid.NewGuid(), "user", DateTime.UtcNow,
            null, "Private message", "[]").Value);
        _dbContext.ChecklistTemplates.Add(
            ChecklistTemplate.Create(userId, "Morning", ["Item"]).Value);

        _dbContext.GoogleCalendarSyncSuggestions.Add(
            GoogleCalendarSyncSuggestion.Create(
                userId, $"evt-{userId}", "Event", DateTime.UtcNow, "{}", DateTime.UtcNow));

        _dbContext.AgentStepUpChallenges.Add(
            AgentStepUpChallengeState.Create(userId, Guid.NewGuid(), "hash", DateTime.UtcNow.AddMinutes(5)));

        var capability = _catalogService.GetCapability(AgentCapabilityIds.ApiKeysManage)!;
        _dbContext.PendingAgentOperations.Add(
            PendingAgentOperationState.Create(new PendingAgentOperationStateCreateRequest
            {
                UserId = userId,
                Capability = capability,
                OperationId = "op",
                ArgumentsJson = "{}",
                Summary = "Summary",
                OperationFingerprint = $"fp-{userId}",
                Surface = AgentExecutionSurface.Mcp,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
            }));

        _dbContext.AgentAuditLogs.Add(
            AgentAuditLog.Create(new AgentAuditEntry(
                userId,
                AgentCapabilityIds.ApiKeysManage,
                "Claude",
                AgentExecutionSurface.Mcp,
                AgentAuthMethod.ApiKey,
                AgentRiskClass.Low,
                AgentPolicyDecisionStatus.Allowed,
                AgentOperationStatus.Succeeded)));

        _dbContext.PendingClarifications.Add(
            PendingClarification.Create(
                userId, "create_habit", "{}", "title", "What title?", "[]", DateTime.UtcNow.AddMinutes(5)));
    }

    private User SeedUser(Guid userId, string email)
    {
        var user = User.Create("Test User", email).Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        _dbContext.Users.Add(user);
        return user;
    }

    [Fact]
    public async Task Handle_ResetWithUserConcurrencyToken_CommitsDeletionsAndCutoffWithoutRetry()
    {
        var user = SeedUser(_userId, "target@example.com");
        SeedUser(_otherUserId, "other@example.com");
        SeedForUser(_userId);
        SeedForUser(_otherUserId);
        user.AddXp(123);
        user.CompleteOnboarding();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var beforeResetAtUtc = DateTime.UtcNow;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 6, 15));
        var unitOfWork = new UnitOfWork(_dbContext, new DatabaseConnectionSettings(), _eventCollector);
        var handler = new ResetAccountCommandHandler(
            new GenericRepository<User>(_dbContext), _repository, unitOfWork, dates, cache);
        var retry = new ConcurrencyRetryBehavior<ResetAccountCommand, Result>(unitOfWork);
        var command = new ResetAccountCommand(_userId);

        var result = await retry.Handle(command, ct => handler.Handle(command, ct), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventCollector.Received(1).MarkResync(_userId);
        _dbContext.ChangeTracker.Clear();
        var saved = await _dbContext.Users.SingleAsync(u => u.Id == _userId);
        saved.TotalXp.Should().Be(0);
        saved.HasCompletedOnboarding.Should().BeFalse();
        saved.ConversationHistoryClearedAtUtc.Should().NotBeNull();
        saved.ConversationHistoryClearedAtUtc.Should().BeOnOrAfter(beforeResetAtUtc);
        _dbContext.Entry(saved).Property<uint>("xmin").CurrentValue.Should().Be(1);
        (await _dbContext.ConversationTurns.CountAsync(turn => turn.UserId == _userId)).Should().Be(0);
        (await _dbContext.ConversationTurns.CountAsync(turn => turn.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Users.SingleAsync(u => u.Id == _otherUserId)).ConversationHistoryClearedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAllUserDataAsync_PreservesAttributedAiUsage()
    {
        SeedUser(_userId, "target@example.com");
        _dbContext.AiUsageDaily.Add(AiUsageDaily.Create(
            DateOnly.FromDateTime(DateTime.UtcNow),
            "gpt-4.1-mini",
            "astra_chat",
            new AiUsageTotals(1, 0, 100, 50, 150, 0.001m),
            _userId));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _repository.DeleteAllUserDataAsync(_userId);

        _eventCollector.Received(1).MarkResync(_userId);

        (await _dbContext.AiUsageDaily.AnyAsync(u => u.UserId == _userId)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAllUserDataAsync_RemovesNewlyCascadedTablesForUser_LeavesOtherUserData()
    {
        SeedUser(_userId, "target@example.com");
        SeedUser(_otherUserId, "other@example.com");
        SeedForUser(_userId);
        SeedForUser(_otherUserId);

        _dbContext.Referrals.Add(Referral.Create(_userId, Guid.NewGuid()));
        _dbContext.Referrals.Add(Referral.Create(Guid.NewGuid(), _userId));
        _dbContext.Referrals.Add(Referral.Create(_otherUserId, Guid.NewGuid()));
        await _dbContext.SaveChangesAsync();

        await _repository.DeleteAllUserDataAsync(_userId);

        (await _dbContext.ConversationTurns.CountAsync(turn => turn.UserId == _userId)).Should().Be(0);
        (await _dbContext.ConversationTurns.CountAsync(turn => turn.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.ChecklistTemplates.CountAsync(ct => ct.UserId == _userId)).Should().Be(0);
        (await _dbContext.GoogleCalendarSyncSuggestions.CountAsync(s => s.UserId == _userId)).Should().Be(0);
        (await _dbContext.AgentStepUpChallenges.CountAsync(c => c.UserId == _userId)).Should().Be(0);
        (await _dbContext.PendingAgentOperations.CountAsync(o => o.UserId == _userId)).Should().Be(0);
        (await _dbContext.AgentAuditLogs.CountAsync(a => a.UserId == _userId)).Should().Be(0);
        (await _dbContext.PendingClarifications.CountAsync(pc => pc.UserId == _userId)).Should().Be(0);
        (await _dbContext.Referrals.CountAsync(r => r.ReferrerId == _userId || r.ReferredUserId == _userId))
            .Should().Be(0);

        (await _dbContext.ChecklistTemplates.CountAsync(ct => ct.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.GoogleCalendarSyncSuggestions.CountAsync(s => s.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.AgentStepUpChallenges.CountAsync(c => c.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.PendingAgentOperations.CountAsync(o => o.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.AgentAuditLogs.CountAsync(a => a.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.PendingClarifications.CountAsync(pc => pc.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Referrals.CountAsync(r => r.ReferrerId == _otherUserId)).Should().Be(1);
    }

    private async Task SeedOrphanProneDataForUser(Guid userId)
    {
        var tag = Tag.Create(userId, $"tag-{userId}", "#fff").Value;
        _dbContext.Tags.Add(tag);

        var goal = Goal.Create(userId, "Goal", 10m, "reps").Value;
        _dbContext.Goals.Add(goal);

        var habit = Habit.Create(new HabitCreateParams(
            userId, "Habit", FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        habit.Log(today, "note");
        habit.Log(today.AddDays(-1), "note");
        habit.Unlog(today.AddDays(-1));
        habit.AddTag(tag);
        habit.AddGoal(goal);
        _dbContext.Habits.Add(habit);

        goal.UpdateProgress(5m);
        _dbContext.GoalProgressLogs.Add(GoalProgressLog.Create(goal.Id, 0m, 5m, "progress"));
        var deletedProgressLog = GoalProgressLog.Create(goal.Id, 5m, 8m, "progress");
        deletedProgressLog.SoftDelete();
        _dbContext.GoalProgressLogs.Add(deletedProgressLog);

        _dbContext.Notifications.Add(Notification.Create(userId, "Title", "Body"));
        var deletedNotification = Notification.Create(userId, "Deleted", "Body");
        deletedNotification.SoftDelete();
        _dbContext.Notifications.Add(deletedNotification);
        _dbContext.UserAchievements.Add(UserAchievement.Create(userId, "first_habit"));
        _dbContext.XpAwardLogs.Add(XpAwardLog.Create(
            userId,
            100,
            XpAwardSource.GoalCompleted,
            goal.Id,
            DateTime.UtcNow));
        _dbContext.PushSubscriptions.Add(
            PushSubscription.Create(userId, $"https://fcm.googleapis.com/fcm/send/{userId}", "p256dh", "auth").Value);
        _dbContext.StreakFreezes.Add(StreakFreeze.Create(userId, DateOnly.FromDateTime(DateTime.UtcNow), StreakFreezeOrigin.Manual));
        _dbContext.ApiKeys.Add(
            ApiKey.Create(userId, "key", ["habits:read"]).Value.Entity);
        _dbContext.ClosedMonthRecaps.Add(
            ClosedMonthRecap.Create(
                userId,
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                "{\"period\":\"month\"}").Value);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task DeleteAllUserDataAsync_EmptiesEveryOrphanProneTable_ForUserOnly()
    {
        SeedUser(_userId, "target@example.com");
        SeedUser(_otherUserId, "other@example.com");
        await SeedOrphanProneDataForUser(_userId);
        await SeedOrphanProneDataForUser(_otherUserId);
        await _dbContext.SaveChangesAsync();

        await _repository.DeleteAllUserDataAsync(_userId);

        (await _dbContext.Habits.IgnoreQueryFilters().CountAsync(h => h.UserId == _userId)).Should().Be(0);
        (await _dbContext.Goals.IgnoreQueryFilters().CountAsync(g => g.UserId == _userId)).Should().Be(0);
        (await _dbContext.Tags.IgnoreQueryFilters().CountAsync(t => t.UserId == _userId)).Should().Be(0);
        (await _dbContext.Notifications.IgnoreQueryFilters().CountAsync(n => n.UserId == _userId)).Should().Be(0);
        (await _dbContext.UserAchievements.CountAsync(ua => ua.UserId == _userId)).Should().Be(0);
        (await _dbContext.XpAwardLogs.CountAsync(award => award.UserId == _userId)).Should().Be(0);
        (await _dbContext.HabitLogs.IgnoreQueryFilters()
            .CountAsync(l => _dbContext.Habits.IgnoreQueryFilters()
                .Any(h => h.Id == l.HabitId && h.UserId == _userId))).Should().Be(0);
        (await _dbContext.GoalProgressLogs.IgnoreQueryFilters()
            .CountAsync(l => _dbContext.Goals.IgnoreQueryFilters()
                .Any(g => g.Id == l.GoalId && g.UserId == _userId))).Should().Be(0);
        (await _dbContext.HabitLogs.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await _dbContext.GoalProgressLogs.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await _dbContext.PushSubscriptions.CountAsync(p => p.UserId == _userId)).Should().Be(0);
        (await _dbContext.StreakFreezes.CountAsync(sf => sf.UserId == _userId)).Should().Be(0);
        (await _dbContext.ApiKeys.CountAsync(k => k.UserId == _userId)).Should().Be(0);
        (await _dbContext.ClosedMonthRecaps.CountAsync(recap => recap.UserId == _userId)).Should().Be(0);

        (await _dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM \"HabitTags\"").ToListAsync()).Single().Should().Be(1);
        (await _dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM \"HabitGoals\"").ToListAsync()).Single().Should().Be(1);

        (await _dbContext.Habits.IgnoreQueryFilters().CountAsync(h => h.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Goals.IgnoreQueryFilters().CountAsync(g => g.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Tags.IgnoreQueryFilters().CountAsync(t => t.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Notifications.IgnoreQueryFilters().CountAsync(n => n.UserId == _otherUserId)).Should().Be(2);
        (await _dbContext.ClosedMonthRecaps.CountAsync(recap => recap.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.XpAwardLogs.CountAsync(award => award.UserId == _otherUserId)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteAllUserDataAsync_RemovesApiKeysAndPushSubscriptions_ForTargetUserOnly()
    {
        SeedUser(_userId, "target@example.com");
        SeedUser(_otherUserId, "other@example.com");

        _dbContext.ApiKeys.Add(ApiKey.Create(_userId, "cli", ["habits:read"]).Value.Entity);
        _dbContext.ApiKeys.Add(ApiKey.Create(_userId, "automation", ["habits:write"]).Value.Entity);
        _dbContext.ApiKeys.Add(ApiKey.Create(_otherUserId, "cli", ["habits:read"]).Value.Entity);

        _dbContext.PushSubscriptions.Add(
            PushSubscription.Create(_userId, "https://fcm.googleapis.com/fcm/send/target-web", "p256dh", "auth").Value);
        _dbContext.PushSubscriptions.Add(
            PushSubscription.Create(_userId, "https://push/target-device", PushSubscription.FcmSentinel, "auth").Value);
        _dbContext.PushSubscriptions.Add(
            PushSubscription.Create(_otherUserId, "https://fcm.googleapis.com/fcm/send/other", "p256dh", "auth").Value);
        await _dbContext.SaveChangesAsync();

        await _repository.DeleteAllUserDataAsync(_userId);

        (await _dbContext.ApiKeys.CountAsync(k => k.UserId == _userId)).Should().Be(0);
        (await _dbContext.PushSubscriptions.CountAsync(p => p.UserId == _userId)).Should().Be(0);

        (await _dbContext.ApiKeys.CountAsync(k => k.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.PushSubscriptions.CountAsync(p => p.UserId == _otherUserId)).Should().Be(1);
    }

    private sealed class SqliteCompatOrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedNever();

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    var defaultSql = property.GetDefaultValueSql();
                    if (defaultSql is not null && defaultSql.Contains("::", StringComparison.Ordinal))
                        property.SetDefaultValueSql(null);
                }

                foreach (var index in entityType.GetIndexes())
                    index.SetFilter(null);
            }
        }
    }
}
