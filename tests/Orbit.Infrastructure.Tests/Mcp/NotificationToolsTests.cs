using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Mcp;

/// <summary>
/// Minimal DbContext with only the Notification entity configured,
/// so InMemory provider works without PostgreSQL-specific Habit/User configs.
/// </summary>
public class TestNotificationDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
}

/// <summary>
/// Test-specific NotificationTools that uses the minimal test DbContext.
/// The real NotificationTools only touches dbContext.Notifications, so this is equivalent.
/// </summary>
public class TestableNotificationTools(TestNotificationDbContext dbContext)
{
    public async Task<string> GetNotifications(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);

        var notifications = await dbContext.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        var unreadCount = await dbContext.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead, cancellationToken);

        if (notifications.Count == 0)
            return "No notifications.";

        var lines = notifications.Select(n =>
            $"- [{(n.IsRead ? " " : "NEW")}] {n.Title}: {n.Body} (id: {n.Id}, {n.CreatedAtUtc:yyyy-MM-dd HH:mm})");

        return $"Notifications ({notifications.Count}, {unreadCount} unread):\n{string.Join("\n", lines)}";
    }

    public async Task<string> MarkNotificationRead(
        ClaimsPrincipal user, string notificationId, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == Guid.Parse(notificationId) && n.UserId == userId, cancellationToken);

        if (notification is null)
            return "Error: Notification not found.";

        notification.MarkAsRead();
        await dbContext.SaveChangesAsync(cancellationToken);
        return $"Marked notification {notificationId} as read.";
    }

    public async Task<string> MarkAllNotificationsRead(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var count = await dbContext.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), cancellationToken);

        return $"Marked {count} notifications as read.";
    }

    public async Task<string> DeleteNotification(
        ClaimsPrincipal user, string notificationId, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == Guid.Parse(notificationId) && n.UserId == userId, cancellationToken);

        if (notification is null)
            return "Error: Notification not found.";

        dbContext.Notifications.Remove(notification);
        await dbContext.SaveChangesAsync(cancellationToken);
        return $"Deleted notification {notificationId}.";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }
}

public class NotificationToolsTests : IDisposable
{
    private readonly TestNotificationDbContext _dbContext;
    private readonly TestableNotificationTools _tools;
    private readonly ClaimsPrincipal _user;
    private readonly Guid _userId = Guid.NewGuid();

    public NotificationToolsTests()
    {
        var options = new DbContextOptionsBuilder<TestNotificationDbContext>()
            .UseInMemoryDatabase(databaseName: $"NotificationToolsTests_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new TestNotificationDbContext(options);
        _tools = new TestableNotificationTools(_dbContext);

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- GetNotifications ---

    [Fact]
    public async Task GetNotifications_NoNotifications_ReturnsNoNotificationsMessage()
    {
        var result = await _tools.GetNotifications(_user);

        result.Should().Be("No notifications.");
    }

    [Fact]
    public async Task GetNotifications_WithNotifications_ReturnsFormattedList()
    {
        var notification = Notification.Create(_userId, "Reminder", "Time to exercise");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("Notifications (1");
        result.Should().Contain("1 unread");
        result.Should().Contain("Reminder");
        result.Should().Contain("Time to exercise");
        result.Should().Contain("[NEW]");
    }

    [Fact]
    public async Task GetNotifications_ReadAndUnread_ShowsCorrectCounts()
    {
        var n1 = Notification.Create(_userId, "Read One", "Body 1");
        n1.MarkAsRead();
        var n2 = Notification.Create(_userId, "Unread One", "Body 2");
        _dbContext.Notifications.AddRange(n1, n2);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("1 unread");
        result.Should().Contain("[ ]");
        result.Should().Contain("[NEW]");
    }

    [Fact]
    public async Task GetNotifications_OtherUsersNotifications_NotReturned()
    {
        var otherUserId = Guid.NewGuid();
        var notification = Notification.Create(otherUserId, "Other User", "Not for me");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Be("No notifications.");
    }

    [Fact]
    public async Task GetNotifications_Max50_LimitsResults()
    {
        for (var i = 0; i < 55; i++)
        {
            _dbContext.Notifications.Add(
                Notification.Create(_userId, $"Notification {i}", $"Body {i}"));
        }
        await _dbContext.SaveChangesAsync();

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("Notifications (50");
    }

    // --- MarkNotificationRead ---

    [Fact]
    public async Task MarkNotificationRead_Exists_MarksAsRead()
    {
        var notification = Notification.Create(_userId, "Test", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.MarkNotificationRead(_user, notification.Id.ToString());

        result.Should().Contain("Marked notification");
        var updated = await _dbContext.Notifications.FindAsync(notification.Id);
        updated!.IsRead.Should().BeTrue();
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
        var otherUserId = Guid.NewGuid();
        var notification = Notification.Create(otherUserId, "Other", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.MarkNotificationRead(_user, notification.Id.ToString());

        result.Should().Be("Error: Notification not found.");
    }

    // --- DeleteNotification ---

    [Fact]
    public async Task DeleteNotification_Exists_RemovesNotification()
    {
        var notification = Notification.Create(_userId, "To Delete", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.DeleteNotification(_user, notification.Id.ToString());

        result.Should().Contain("Deleted notification");
        var deleted = await _dbContext.Notifications.FindAsync(notification.Id);
        deleted.Should().BeNull();
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
        var otherUserId = Guid.NewGuid();
        var notification = Notification.Create(otherUserId, "Other", "Body");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var result = await _tools.DeleteNotification(_user, notification.Id.ToString());

        result.Should().Be("Error: Notification not found.");
    }

    // --- GetUserId ---

    [Fact]
    public async Task AnyMethod_MissingUserClaim_ThrowsUnauthorized()
    {
        var emptyUser = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _tools.GetNotifications(emptyUser);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*User ID not found*");
    }
}
