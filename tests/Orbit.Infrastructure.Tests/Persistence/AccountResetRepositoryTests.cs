using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AccountResetRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly AccountResetRepository _repository;
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
        _repository = new AccountResetRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedForUser(Guid userId)
    {
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

    private void SeedUser(Guid userId, string email)
    {
        var user = User.Create("Test User", email).Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        _dbContext.Users.Add(user);
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
            userId, "Habit", FrequencyUnit.Day, 1)).Value;
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

        _dbContext.UserFacts.Add(UserFact.Create(userId, "likes mornings", "preference").Value);
        _dbContext.Notifications.Add(Notification.Create(userId, "Title", "Body"));
        var deletedNotification = Notification.Create(userId, "Deleted", "Body");
        deletedNotification.SoftDelete();
        _dbContext.Notifications.Add(deletedNotification);
        _dbContext.UserAchievements.Add(UserAchievement.Create(userId, "first_habit"));
        _dbContext.PushSubscriptions.Add(
            PushSubscription.Create(userId, $"https://push/{userId}", "p256dh", "auth").Value);
        _dbContext.StreakFreezes.Add(StreakFreeze.Create(userId, DateOnly.FromDateTime(DateTime.UtcNow)));
        _dbContext.SentStreakFreezeAlerts.Add(
            SentStreakFreezeAlert.Create(userId, DateOnly.FromDateTime(DateTime.UtcNow)));
        _dbContext.ApiKeys.Add(
            ApiKey.Create(userId, "key", ["habits:read"]).Value.Entity);

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
        (await _dbContext.UserFacts.IgnoreQueryFilters().CountAsync(f => f.UserId == _userId)).Should().Be(0);
        (await _dbContext.Notifications.IgnoreQueryFilters().CountAsync(n => n.UserId == _userId)).Should().Be(0);
        (await _dbContext.UserAchievements.CountAsync(ua => ua.UserId == _userId)).Should().Be(0);
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
        (await _dbContext.SentStreakFreezeAlerts.CountAsync(a => a.UserId == _userId)).Should().Be(0);
        (await _dbContext.ApiKeys.CountAsync(k => k.UserId == _userId)).Should().Be(0);

        (await _dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM \"HabitTags\"").ToListAsync()).Single().Should().Be(1);
        (await _dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM \"HabitGoals\"").ToListAsync()).Single().Should().Be(1);

        (await _dbContext.Habits.IgnoreQueryFilters().CountAsync(h => h.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Goals.IgnoreQueryFilters().CountAsync(g => g.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Tags.IgnoreQueryFilters().CountAsync(t => t.UserId == _otherUserId)).Should().Be(1);
        (await _dbContext.Notifications.IgnoreQueryFilters().CountAsync(n => n.UserId == _otherUserId)).Should().Be(2);
    }

    private sealed class SqliteCompatOrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

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
