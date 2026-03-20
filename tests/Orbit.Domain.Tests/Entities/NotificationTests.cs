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
            habitId: habitId);

        notification.UserId.Should().Be(ValidUserId);
        notification.Title.Should().Be("Reminder");
        notification.Body.Should().Be("Time to exercise!");
        notification.Url.Should().Be("/habits");
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
}
