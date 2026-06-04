using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class NotificationToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly NotificationTools _tools;
    private readonly ClaimsPrincipal _user;

    public NotificationToolsTests()
    {
        _tools = new NotificationTools(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static NotificationItemDto Item(string title, string body, bool isRead) =>
        new(Guid.NewGuid(), title, body, null, null, isRead, DateTime.UtcNow);

    [Fact]
    public async Task GetNotifications_NoNotifications_ReturnsNoNotificationsMessage()
    {
        _mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GetNotificationsResponse([], 0)));

        var result = await _tools.GetNotifications(_user);

        result.Should().Be("No notifications.");
    }

    [Fact]
    public async Task GetNotifications_WithNotifications_ReturnsFormattedList()
    {
        var response = new GetNotificationsResponse(
            [Item("Reminder", "Time to exercise", isRead: false)],
            1);
        _mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("Notifications (1, 1 unread)");
        result.Should().Contain("Reminder");
        result.Should().Contain("Time to exercise");
        result.Should().Contain("[NEW]");
    }

    [Fact]
    public async Task GetNotifications_ReadAndUnread_ShowsCorrectCounts()
    {
        var response = new GetNotificationsResponse(
            [Item("Read", "Body 1", isRead: true), Item("Unread", "Body 2", isRead: false)],
            1);
        _mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetNotifications(_user);

        result.Should().Contain("Notifications (2, 1 unread)");
        result.Should().Contain("[ ]");
        result.Should().Contain("[NEW]");
    }

    [Fact]
    public async Task GetNotifications_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GetNotificationsResponse>("Boom"));

        var result = await _tools.GetNotifications(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task MarkNotificationRead_Success_ReturnsMarkedMessage()
    {
        var notificationId = Guid.NewGuid();
        _mediator.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.MarkNotificationRead(_user, notificationId.ToString());

        result.Should().Be($"Marked notification {notificationId} as read.");
    }

    [Fact]
    public async Task MarkNotificationRead_NotFound_ReturnsError()
    {
        _mediator.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.NotificationNotFound, ErrorCodes.NotificationNotFound));

        var result = await _tools.MarkNotificationRead(_user, Guid.NewGuid().ToString());

        result.Should().Be("Error: Notification not found.");
    }

    [Fact]
    public async Task MarkAllNotificationsRead_ReturnsCountMessage()
    {
        _mediator.Send(Arg.Any<MarkAllNotificationsReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(2));

        var result = await _tools.MarkAllNotificationsRead(_user);

        result.Should().Be("Marked 2 notifications as read.");
    }

    [Fact]
    public async Task DeleteNotification_Success_ReturnsDeletedMessage()
    {
        var notificationId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.DeleteNotification(_user, notificationId.ToString());

        result.Should().Be($"Deleted notification {notificationId}.");
    }

    [Fact]
    public async Task DeleteNotification_NotFound_IsIdempotentAndReturnsDeletedMessage()
    {
        var notificationId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.DeleteNotification(_user, notificationId.ToString());

        result.Should().Be($"Deleted notification {notificationId}.");
    }

    [Fact]
    public async Task GetNotifications_MissingUserClaim_ThrowsUnauthorized()
    {
        var emptyUser = new ClaimsPrincipal(new ClaimsIdentity());

        var act = () => _tools.GetNotifications(emptyUser);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*User ID not found*");
    }
}
