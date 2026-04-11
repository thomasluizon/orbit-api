using System.Security.Claims;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Mcp;

public class NotificationToolsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NotificationOnlyOrbitDbContext _dbContext;
    private readonly NotificationTools _tools;
    private readonly ClaimsPrincipal _user;
    private readonly Guid _userId = Guid.NewGuid();

    public NotificationToolsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new NotificationOnlyOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();
        _tools = new NotificationTools(_dbContext);
        _user = CreateUser(_userId);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class NotificationOnlyOrbitDbContext(DbContextOptions<OrbitDbContext> options)
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
            modelBuilder.Ignore<Goal>();
            modelBuilder.Ignore<GoalProgressLog>();
            modelBuilder.Ignore<Referral>();
            modelBuilder.Ignore<UserAchievement>();
            modelBuilder.Ignore<StreakFreeze>();
            modelBuilder.Ignore<UserSession>();
            modelBuilder.Ignore<ApiKey>();
            modelBuilder.Ignore<ChecklistTemplate>();
            modelBuilder.Ignore<AppFeatureFlag>();
            modelBuilder.Ignore<ContentBlock>();
            modelBuilder.Ignore<GoogleCalendarSyncSuggestion>();

            modelBuilder.Entity<Notification>(entity =>
            {
                entity.HasKey(n => n.Id);
                entity.HasIndex(n => new { n.UserId, n.IsRead });
                entity.HasIndex(n => n.Url);
            });
        }
    }

    private static ClaimsPrincipal CreateUser(Guid userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task GetNotifications_NoNotifications_ReturnsNoNotificationsMessage()
    {
        var result = await _tools.GetNotifications(_user);

        result.Should().Be("No notifications.");
    }

    [Fact]
    public async Task GetNotifications_WithNotifications_ReturnsFormattedList()
    {
        _dbContext.Notifications.Add(Notification.Create(_userId, "Reminder", "Time to exercise"));
        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("Notifications (1, 1 unread)");
        result.Should().Contain("Reminder");
        result.Should().Contain("Time to exercise");
        result.Should().Contain("[NEW]");
    }

    [Fact]
    public async Task GetNotifications_ReadAndUnread_ShowsCorrectCounts()
    {
        var read = Notification.Create(_userId, "Read", "Body 1");
        read.MarkAsRead();
        var unread = Notification.Create(_userId, "Unread", "Body 2");
        _dbContext.Notifications.AddRange(read, unread);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("Notifications (2, 1 unread)");
        result.Should().Contain("[ ]");
        result.Should().Contain("[NEW]");
    }

    [Fact]
    public async Task GetNotifications_OtherUsersNotifications_NotReturned()
    {
        _dbContext.Notifications.Add(Notification.Create(Guid.NewGuid(), "Other User", "Not for me"));
        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Be("No notifications.");
    }

    [Fact]
    public async Task GetNotifications_Max50_LimitsResults()
    {
        for (var i = 0; i < 55; i++)
            _dbContext.Notifications.Add(Notification.Create(_userId, $"Notification {i}", $"Body {i}"));

        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("Notifications (50, 55 unread)");
    }

    [Fact]
    public async Task MarkNotificationRead_Exists_MarksAsRead()
    {
        var notification = Notification.Create(_userId, "Test", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.MarkNotificationRead(_user, notification.Id.ToString());

        result.Should().Be($"Marked notification {notification.Id} as read.");
        notification.IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task MarkNotificationRead_NotFound_ReturnsError()
    {
        var result = await _tools.MarkNotificationRead(_user, Guid.NewGuid().ToString());

        result.Should().Be("Error: Notification not found.");
    }

    [Fact]
    public async Task MarkNotificationRead_OtherUsersNotification_ReturnsError()
    {
        var notification = Notification.Create(Guid.NewGuid(), "Other", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.MarkNotificationRead(_user, notification.Id.ToString());

        result.Should().Be("Error: Notification not found.");
    }

    [Fact]
    public async Task MarkAllNotificationsRead_MarksUnreadNotificationsForCurrentUser()
    {
        var unreadOne = Notification.Create(_userId, "Unread 1", "Body 1");
        var unreadTwo = Notification.Create(_userId, "Unread 2", "Body 2");
        var alreadyRead = Notification.Create(_userId, "Read", "Body 3");
        alreadyRead.MarkAsRead();
        var otherUser = Notification.Create(Guid.NewGuid(), "Other", "Body 4");

        _dbContext.Notifications.AddRange(unreadOne, unreadTwo, alreadyRead, otherUser);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.MarkAllNotificationsRead(_user);

        await using var verificationContext = new NotificationOnlyOrbitDbContext(
            new DbContextOptionsBuilder<OrbitDbContext>()
                .UseSqlite(_connection)
                .Options);
        var notifications = await verificationContext.Notifications
            .AsNoTracking()
            .Where(n => n.Id == unreadOne.Id || n.Id == unreadTwo.Id || n.Id == alreadyRead.Id || n.Id == otherUser.Id)
            .ToDictionaryAsync(n => n.Id);

        result.Should().Be("Marked 2 notifications as read.");
        notifications[unreadOne.Id].IsRead.Should().BeTrue();
        notifications[unreadTwo.Id].IsRead.Should().BeTrue();
        notifications[alreadyRead.Id].IsRead.Should().BeTrue();
        notifications[otherUser.Id].IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteNotification_Exists_RemovesNotification()
    {
        var notification = Notification.Create(_userId, "To Delete", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.DeleteNotification(_user, notification.Id.ToString());

        result.Should().Be($"Deleted notification {notification.Id}.");
        _dbContext.Notifications.Any(n => n.Id == notification.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteNotification_NotFound_ReturnsError()
    {
        var result = await _tools.DeleteNotification(_user, Guid.NewGuid().ToString());

        result.Should().Be("Error: Notification not found.");
    }

    [Fact]
    public async Task DeleteNotification_OtherUsersNotification_ReturnsError()
    {
        var notification = Notification.Create(Guid.NewGuid(), "Other", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.DeleteNotification(_user, notification.Id.ToString());

        result.Should().Be("Error: Notification not found.");
    }

    [Fact]
    public async Task AnyMethod_MissingUserClaim_ThrowsUnauthorized()
    {
        var emptyUser = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _tools.GetNotifications(emptyUser);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*User ID not found*");
    }
}
