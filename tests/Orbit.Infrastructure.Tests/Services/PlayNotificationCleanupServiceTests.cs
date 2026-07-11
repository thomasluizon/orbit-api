using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Verifies the retention boundaries of <see cref="PlayNotificationCleanupService"/>: Play RTDN
/// dedup records purge after 30 days and Stripe webhook dedup records after 90 days, while records
/// inside their provider redelivery window survive. Runs against in-memory SQLite so the service's
/// bulk <c>ExecuteDeleteAsync</c> executes as real SQL.
/// </summary>
public sealed class PlayNotificationCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public PlayNotificationCleanupServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(_ => _dbContext);
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_PurgesRecordsOlderThanRetentionAndKeepsRecentOnes()
    {
        AddPlayNotification("play-recent", DaysAgo(29));
        AddPlayNotification("play-old", DaysAgo(31));
        AddStripeEvent("stripe-recent", DaysAgo(89));
        AddStripeEvent("stripe-old", DaysAgo(91));
        await _dbContext.SaveChangesAsync();

        var service = new PlayNotificationCleanupService(_scopeFactory, NullLogger<PlayNotificationCleanupService>.Instance);
        await service.RunAsync(CancellationToken.None);

        var remainingPlay = await _dbContext.ProcessedPlayNotifications.Select(n => n.MessageId).ToListAsync();
        var remainingStripe = await _dbContext.ProcessedStripeEvents.Select(e => e.EventId).ToListAsync();

        remainingPlay.Should().ContainSingle().Which.Should().Be("play-recent");
        remainingStripe.Should().ContainSingle().Which.Should().Be("stripe-recent");
    }

    [Fact]
    public async Task RunAsync_NothingExpired_DeletesNothing()
    {
        AddPlayNotification("play-fresh", DaysAgo(1));
        AddStripeEvent("stripe-fresh", DaysAgo(1));
        await _dbContext.SaveChangesAsync();

        var service = new PlayNotificationCleanupService(_scopeFactory, NullLogger<PlayNotificationCleanupService>.Instance);
        await service.RunAsync(CancellationToken.None);

        (await _dbContext.ProcessedPlayNotifications.CountAsync()).Should().Be(1);
        (await _dbContext.ProcessedStripeEvents.CountAsync()).Should().Be(1);
    }

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.AddDays(-days);

    private void AddPlayNotification(string messageId, DateTime processedAtUtc)
    {
        var notification = ProcessedPlayNotification.Create(messageId);
        SetProcessedAt(notification, processedAtUtc);
        _dbContext.ProcessedPlayNotifications.Add(notification);
    }

    private void AddStripeEvent(string eventId, DateTime processedAtUtc)
    {
        var stripeEvent = ProcessedStripeEvent.Create(eventId);
        SetProcessedAt(stripeEvent, processedAtUtc);
        _dbContext.ProcessedStripeEvents.Add(stripeEvent);
    }

    private static void SetProcessedAt(ProcessedExternalEvent target, DateTime processedAtUtc) =>
        typeof(ProcessedExternalEvent)
            .GetProperty(nameof(ProcessedExternalEvent.ProcessedAtUtc))!
            .SetValue(target, processedAtUtc);

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
