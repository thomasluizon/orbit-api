using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AccountDeletionServiceDbTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly AccountDeletionService _service;

    public AccountDeletionServiceDbTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        var serviceProvider = new ServiceCollection()
            .AddSingleton(_dbContext)
            .AddSingleton<IAccountResetRepository>(new AccountResetRepository(_dbContext))
            .BuildServiceProvider();

        _service = new AccountDeletionService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AccountDeletionService>.Instance,
            new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CleanupStaleSentRecords_RemovesStreakFreezeAlertsOlderThan90Days_KeepsRecent()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "gc@example.com");

        var stale = SentStreakFreezeAlert.Create(userId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-91)));
        var recent = SentStreakFreezeAlert.Create(userId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-89)));
        _dbContext.SentStreakFreezeAlerts.AddRange(stale, recent);
        await _dbContext.SaveChangesAsync();

        await _service.CleanupStaleSentRecords(CancellationToken.None);

        var remaining = await _dbContext.SentStreakFreezeAlerts.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].FrozenDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-89)));
    }

    [Fact]
    public async Task DeleteAllUserDataAsync_RemovesSentProactiveCheckins()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "proactive@example.com");
        _dbContext.SentProactiveCheckins.Add(
            SentProactiveCheckin.Create(userId, DateOnly.FromDateTime(DateTime.UtcNow)));
        await _dbContext.SaveChangesAsync();

        await new AccountResetRepository(_dbContext).DeleteAllUserDataAsync(userId);

        (await _dbContext.SentProactiveCheckins.AnyAsync(p => p.UserId == userId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_DeletesOnlyPastDueDeactivatedUsers_PerUserScope()
    {
        var pastDue = Guid.NewGuid();
        var futureDated = Guid.NewGuid();
        var active = Guid.NewGuid();
        SeedDeactivatedUser(pastDue, "pastdue@example.com", DateTime.UtcNow.AddDays(-1));
        SeedDeactivatedUser(futureDated, "future@example.com", DateTime.UtcNow.AddDays(30));
        SeedUser(active, "active@example.com");
        await _dbContext.SaveChangesAsync();

        await _service.RunAsync(CancellationToken.None);

        var remaining = await _dbContext.Users.Select(u => u.Id).ToListAsync();
        remaining.Should().BeEquivalentTo(new[] { futureDated, active });
    }

    private void SeedUser(Guid userId, string email)
    {
        var user = User.Create("Test User", email).Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        _dbContext.Users.Add(user);
    }

    private void SeedDeactivatedUser(Guid userId, string email, DateTime scheduledDeletion)
    {
        var user = User.Create("Test User", email).Value;
        typeof(User).GetProperty("Id")!.SetValue(user, userId);
        user.Deactivate(scheduledDeletion);
        _dbContext.Users.Add(user);
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
