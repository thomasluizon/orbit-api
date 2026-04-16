using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class DistributedRateLimitServiceTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly DistributedRateLimitService _service;

    public DistributedRateLimitServiceTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DistributedRateLimitServiceTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        _service = new DistributedRateLimitService(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TryAcquireAsync_AuthPolicy_BlocksAfterPermitLimit()
    {
        DistributedRateLimitDecision finalDecision = new(true, 0, 0, DateTime.UtcNow);

        for (var attempt = 0; attempt < 6; attempt++)
            finalDecision = await _service.TryAcquireAsync("auth", "ip:127.0.0.1");

        finalDecision.Allowed.Should().BeFalse();
        finalDecision.PermitLimit.Should().Be(5);
        finalDecision.CurrentCount.Should().Be(5);
    }

    [Fact]
    public async Task TryAcquireAsync_ChatPolicy_UsesIndependentPartitions()
    {
        for (var attempt = 0; attempt < 20; attempt++)
            (await _service.TryAcquireAsync("chat", "user:one")).Allowed.Should().BeTrue();

        var blocked = await _service.TryAcquireAsync("chat", "user:one");
        var otherPartition = await _service.TryAcquireAsync("chat", "user:two");

        blocked.Allowed.Should().BeFalse();
        blocked.PermitLimit.Should().Be(20);
        otherPartition.Allowed.Should().BeTrue();
        otherPartition.CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireAsync_RelationalProvider_UsesExecutionStrategyTransactionPath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new RateLimitOnlyOrbitDbContext(options);
        context.Database.EnsureCreated();
        var service = new DistributedRateLimitService(context);

        DistributedRateLimitDecision decision = new(true, 0, 0, DateTime.UtcNow);

        for (var attempt = 0; attempt < 6; attempt++)
            decision = await service.TryAcquireAsync("auth", "ip:203.0.113.10");

        decision.Allowed.Should().BeFalse();
        decision.PermitLimit.Should().Be(5);
        decision.CurrentCount.Should().Be(5);
    }

    private sealed class RateLimitOnlyOrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<User>();
            modelBuilder.Ignore<Habit>();
            modelBuilder.Ignore<HabitLog>();
            modelBuilder.Ignore<UserFact>();
            modelBuilder.Ignore<AppConfig>();
            modelBuilder.Ignore<Tag>();
            modelBuilder.Ignore<PushSubscription>();
            modelBuilder.Ignore<SentReminder>();
            modelBuilder.Ignore<SentSlipAlert>();
            modelBuilder.Ignore<Notification>();
            modelBuilder.Ignore<Goal>();
            modelBuilder.Ignore<GoalProgressLog>();
            modelBuilder.Ignore<Referral>();
            modelBuilder.Ignore<UserAchievement>();
            modelBuilder.Ignore<StreakFreeze>();
            modelBuilder.Ignore<UserSession>();
            modelBuilder.Ignore<ApiKey>();
            modelBuilder.Ignore<PendingAgentOperationState>();
            modelBuilder.Ignore<AgentStepUpChallengeState>();
            modelBuilder.Ignore<AgentAuditLog>();
            modelBuilder.Ignore<ChecklistTemplate>();
            modelBuilder.Ignore<AppFeatureFlag>();
            modelBuilder.Ignore<ContentBlock>();
            modelBuilder.Ignore<GoogleCalendarSyncSuggestion>();

            modelBuilder.Entity<DistributedRateLimitBucket>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.HasIndex(item => new { item.PolicyName, item.PartitionKey, item.WindowStartUtc }).IsUnique();
                entity.Property(item => item.PolicyName).HasMaxLength(64);
                entity.Property(item => item.PartitionKey).HasMaxLength(256);
            });
        }
    }
}
