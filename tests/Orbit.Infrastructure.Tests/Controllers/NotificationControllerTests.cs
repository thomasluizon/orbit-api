using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class NotificationControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<NotificationController> _logger = Substitute.For<ILogger<NotificationController>>();
    private readonly NotificationController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public NotificationControllerTests()
    {
        _controller = new NotificationController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetNotifications ---

    [Fact]
    public async Task GetNotifications_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GetNotificationsResponse([], 0)));

        var result = await _controller.GetNotifications(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetNotifications_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GetNotificationsResponse>("Error"));

        var result = await _controller.GetNotifications(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- MarkAsRead ---

    [Fact]
    public async Task MarkAsRead_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.MarkAsRead(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MarkAsRead_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Notification not found"));

        var result = await _controller.MarkAsRead(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // --- MarkAllAsRead ---

    [Fact]
    public async Task MarkAllAsRead_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<MarkAllNotificationsReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.MarkAllAsRead(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MarkAllAsRead_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<MarkAllNotificationsReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.MarkAllAsRead(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- Delete ---

    [Fact]
    public async Task Delete_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- DeleteAll ---

    [Fact]
    public async Task DeleteAll_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DeleteAllNotificationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DeleteAll(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteAll_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DeleteAllNotificationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.DeleteAll(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- Subscribe ---

    [Fact]
    public async Task Subscribe_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new NotificationController.SubscribeRequest("https://endpoint", "p256dh", "auth");
        var result = await _controller.Subscribe(request, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Subscribe_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new NotificationController.SubscribeRequest("https://endpoint", "p256dh", "auth");
        var result = await _controller.Subscribe(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- Unsubscribe ---

    [Fact]
    public async Task Unsubscribe_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<UnsubscribePushCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new NotificationController.SubscribeRequest("https://endpoint", "p256dh", "auth");
        var result = await _controller.Unsubscribe(request, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Unsubscribe_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UnsubscribePushCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new NotificationController.SubscribeRequest("https://endpoint", "p256dh", "auth");
        var result = await _controller.Unsubscribe(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- TestPush ---

    [Fact]
    public async Task TestPush_Success_ReturnsOk()
    {
        var response = new TestPushNotificationResponse(2, "sent");
        _mediator.Send(Arg.Any<TestPushNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _controller.TestPush(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task TestPush_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<TestPushNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TestPushNotificationResponse>("No subscriptions"));

        var result = await _controller.TestPush(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestPush_FailedStatus_StillReturnsOk()
    {
        var response = new TestPushNotificationResponse(1, "failed", "Send error");
        _mediator.Send(Arg.Any<TestPushNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _controller.TestPush(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
