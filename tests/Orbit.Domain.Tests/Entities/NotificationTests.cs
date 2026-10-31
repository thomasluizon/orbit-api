using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class NotificationTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    [Fact]
    public void Create_SetsAllProperties()
    {
        var habitId = Guid.NewGuid();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var notification = Notification.Create(
            ValidUserId,
            "Reminder",
            "Time to exercise!",
            url: "/habits",
            habitId: habitId,
            dedupeKey: "reminder-key");

        notification.UserId.Should().Be(ValidUserId);
        notification.Title.Should().Be("Reminder");
        notification.Body.Should().Be("Time to exercise!");
        notification.Url.Should().Be("/habits");
        notification.DedupeKey.Should().Be("reminder-key");
        notification.HabitId.Should().Be(habitId);
        notification.IsRead.Should().BeFalse();
        notification.CreatedAtUtc.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void MarkAsRead_SetsIsReadTrue()
    {
        var notification = Notification.Create(ValidUserId, "Title", "Body");
        notification.IsRead.Should().BeFalse();

        notification.MarkAsRead();

        notification.IsRead.Should().BeTrue();
    }

    [Fact]
    public void RefreshReminder_UpdatesTheRowAndMakesItUnread()
    {
        var habitId = Guid.NewGuid();
        var row = Notification.Create(ValidUserId, "Title", "Old", "/", habitId);
        row.MarkAsRead();
        row.SoftDelete();
        var reminder = Notification.Create(ValidUserId, "Updated", "Latest", "/", habitId);
        var sentAtUtc = new DateTime(2027, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        row.RefreshReminder(reminder, "daily-key", sentAtUtc);

        row.Title.Should().Be(reminder.Title);
        row.Body.Should().Be(reminder.Body);
        row.DedupeKey.Should().Be("daily-key");
        row.CreatedAtUtc.Should().Be(sentAtUtc);
        row.IsRead.Should().BeFalse();
        row.IsDeleted.Should().BeFalse();
        row.DeletedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("user")]
    [InlineData("habit")]
    [InlineData("key")]
    [InlineData("time")]
    public void RefreshReminder_RejectsMismatchedIdentityOrInvalidTime(string invalid)
    {
        var habitId = Guid.NewGuid();
        var row = Notification.Create(ValidUserId, "Title", "Old", "/", habitId, "daily-key");
        var reminder = Notification.Create(invalid == "user" ? Guid.NewGuid() : ValidUserId, "Title", "Latest",
            "/", invalid == "habit" ? Guid.NewGuid() : habitId);
        var sentAtUtc = new DateTime(2027, 9, 26, 12, 0, 0,
            invalid == "time" ? DateTimeKind.Unspecified : DateTimeKind.Utc);

        var act = () => row.RefreshReminder(reminder, invalid == "key" ? "different-key" : "daily-key", sentAtUtc);

        act.Should().Throw<ArgumentException>();
        row.Body.Should().Be("Old");
    }

    [Theory]
    [InlineData("goal-deadline-key")]
    [InlineData("//external.example")]
    public void Create_WithNonAppRelativeUrl_Throws(string url)
    {
        var action = () => Notification.Create(ValidUserId, "Title", "Body", url);

        action.Should().Throw<ArgumentException>()
            .WithParameterName("url");
    }
}
