using FluentAssertions;
using NSubstitute;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Notifications;

public class GetNotificationsQueryHandlerTests
{
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly GetNotificationsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetNotificationsQueryHandlerTests()
    {
        _handler = new GetNotificationsQueryHandler(_notificationRepo);
    }

    [Fact]
    public async Task Handle_ReturnsNotificationsWithUnreadCount()
    {
        var notifications = new List<Notification>
        {
            Notification.Create(UserId, "Title 1", "Body 1"),
            Notification.Create(UserId, "Title 2", "Body 2")
        };

        _notificationRepo.FindAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(notifications.AsReadOnly());

        _notificationRepo.CountAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(2);

        var query = new GetNotificationsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.UnreadCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_NoNotifications_ReturnsEmptyResult()
    {
        _notificationRepo.FindAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Notification>().AsReadOnly());

        _notificationRepo.CountAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(0);

        var query = new GetNotificationsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_MapsNotificationFieldsCorrectly()
    {
        var notification = Notification.Create(UserId, "Reminder", "Time to exercise", "/habits/123");

        _notificationRepo.FindAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Notification> { notification }.AsReadOnly());

        _notificationRepo.CountAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(1);

        var query = new GetNotificationsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value.Items[0];
        item.Title.Should().Be("Reminder");
        item.Body.Should().Be("Time to exercise");
        item.Url.Should().Be("/habits/123");
        item.IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MixedReadStatus_ReturnsCorrectUnreadCount()
    {
        var unread = Notification.Create(UserId, "Unread", "Body");
        var read = Notification.Create(UserId, "Read", "Body");
        read.MarkAsRead();

        _notificationRepo.FindAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Notification> { unread, read }.AsReadOnly());

        _notificationRepo.CountAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(1);

        var query = new GetNotificationsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.UnreadCount.Should().Be(1);
    }
}
