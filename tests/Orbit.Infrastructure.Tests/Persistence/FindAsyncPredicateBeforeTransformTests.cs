using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class FindAsyncPredicateBeforeTransformTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();

    private static readonly DateTime BaseTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public FindAsyncPredicateBeforeTransformTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Notification NotificationAt(Guid userId, string title, DateTime createdAtUtc)
    {
        var notification = Notification.Create(userId, title, "body");
        typeof(Notification).GetProperty(nameof(Notification.CreatedAtUtc))!
            .SetValue(notification, createdAtUtc);
        return notification;
    }

    private GetNotificationsQueryHandler Handler() =>
        new(new GenericRepository<Notification>(_dbContext));

    private async Task PersistAsync()
    {
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Handle_ReturnsTargetUsersNotifications_WhenAnotherUserOwnsAllNewestRows()
    {
        for (var i = 1; i <= AppConstants.MaxNotificationsReturned + 10; i++)
            _dbContext.Notifications.Add(
                NotificationAt(_otherUserId, $"other-{i}", BaseTime.AddMinutes(i)));

        var mine = new List<Notification>();
        for (var i = 1; i <= 5; i++)
        {
            var notification = NotificationAt(_userId, $"mine-{i}", BaseTime.AddMinutes(-i));
            mine.Add(notification);
            _dbContext.Notifications.Add(notification);
        }

        await PersistAsync();

        var result = await Handler().Handle(new GetNotificationsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(5);
        result.Value.Items.Select(item => item.Id).Should().BeEquivalentTo(mine.Select(n => n.Id));
    }

    [Fact]
    public async Task Handle_CapsAtMaxReturned_AndKeepsNewestWithinUserScope()
    {
        for (var i = 1; i <= AppConstants.MaxNotificationsReturned + 10; i++)
            _dbContext.Notifications.Add(
                NotificationAt(_otherUserId, $"other-{i}", BaseTime.AddHours(1).AddMinutes(i)));

        var mineByAge = new List<Notification>();
        for (var i = 1; i <= AppConstants.MaxNotificationsReturned + 10; i++)
        {
            var notification = NotificationAt(_userId, $"mine-{i}", BaseTime.AddMinutes(i));
            mineByAge.Add(notification);
            _dbContext.Notifications.Add(notification);
        }

        await PersistAsync();

        var result = await Handler().Handle(new GetNotificationsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var items = result.Value.Items;
        items.Should().HaveCount(AppConstants.MaxNotificationsReturned);

        var expectedNewest = mineByAge
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(AppConstants.MaxNotificationsReturned)
            .Select(n => n.Id);
        items.Select(item => item.Id).Should().Equal(expectedNewest);
        items.Select(item => item.CreatedAtUtc).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Handle_ExcludesSoftDeletedNotifications()
    {
        var active = new List<Notification>();
        for (var i = 1; i <= 3; i++)
        {
            var notification = NotificationAt(_userId, $"active-{i}", BaseTime.AddMinutes(i));
            active.Add(notification);
            _dbContext.Notifications.Add(notification);
        }

        for (var i = 1; i <= 2; i++)
        {
            var deleted = NotificationAt(_userId, $"deleted-{i}", BaseTime.AddMinutes(10 + i));
            deleted.SoftDelete();
            _dbContext.Notifications.Add(deleted);
        }

        await PersistAsync();

        var result = await Handler().Handle(new GetNotificationsQuery(_userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.Id).Should().BeEquivalentTo(active.Select(n => n.Id));
    }

    [Fact]
    public async Task FindAsync_WithIncludeTransform_FiltersByPredicateAndLoadsNavigation()
    {
        var goal = SeedGoalWithProgressLogs(_userId, "mine");
        SeedGoalWithProgressLogs(_otherUserId, "other");
        await PersistAsync();

        var repository = new GenericRepository<Goal>(_dbContext);
        var results = await repository.FindAsync(
            g => g.UserId == _userId,
            q => q.Include(g => g.ProgressLogs));

        results.Should().ContainSingle();
        results[0].Id.Should().Be(goal.Id);
        results[0].ProgressLogs.Should().HaveCount(2);
    }

    [Fact]
    public async Task FindTrackedAsync_WithIncludeTransform_FiltersByPredicateAndLoadsNavigation()
    {
        var goal = SeedGoalWithProgressLogs(_userId, "mine");
        SeedGoalWithProgressLogs(_otherUserId, "other");
        await PersistAsync();

        var repository = new GenericRepository<Goal>(_dbContext);
        var results = await repository.FindTrackedAsync(
            g => g.UserId == _userId,
            q => q.Include(g => g.ProgressLogs));

        results.Should().ContainSingle();
        results[0].Id.Should().Be(goal.Id);
        results[0].ProgressLogs.Should().HaveCount(2);
    }

    [Fact]
    public async Task FindOneTrackedAsync_WithIncludeTransform_FiltersByPredicateAndLoadsNavigation()
    {
        var goal = SeedGoalWithProgressLogs(_userId, "mine");
        SeedGoalWithProgressLogs(_otherUserId, "other");
        await PersistAsync();

        var repository = new GenericRepository<Goal>(_dbContext);
        var found = await repository.FindOneTrackedAsync(
            g => g.Id == goal.Id,
            q => q.Include(g => g.ProgressLogs));

        found.Should().NotBeNull();
        found!.UserId.Should().Be(_userId);
        found.ProgressLogs.Should().HaveCount(2);
    }

    private Goal SeedGoalWithProgressLogs(Guid userId, string title)
    {
        var goal = Goal.Create(userId, title, 10m, "reps").Value;
        _dbContext.Goals.Add(goal);
        _dbContext.GoalProgressLogs.Add(GoalProgressLog.Create(goal.Id, 0m, 3m, "first"));
        _dbContext.GoalProgressLogs.Add(GoalProgressLog.Create(goal.Id, 3m, 7m, "second"));
        return goal;
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
