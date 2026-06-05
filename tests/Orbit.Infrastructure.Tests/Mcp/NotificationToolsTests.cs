using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class NotificationToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly NotificationTools _tools;
    private readonly ClaimsPrincipal _user;

    public NotificationToolsTests()
    {
        _tools = new NotificationTools(_mediator, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static NotificationItemDto Item(string title, string body, bool isRead) =>
        new(Guid.NewGuid(), title, body, null, null, isRead, DateTime.UtcNow);

    private void StubExecutor(AgentOperationStatus status, string? policyReason = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "operation", "operation", AgentRiskClass.Low, AgentConfirmationRequirement.None,
            status, PolicyReason: policyReason));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private async Task<AgentExecuteOperationRequest> CapturedRequestAsync(Func<Task> act)
    {
        await act();
        var calls = _executor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .ToList();
        calls.Should().NotBeEmpty();
        return (AgentExecuteOperationRequest)calls[^1].GetArguments()[0]!;
    }

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
    public async Task MarkNotificationRead_Success_RoutesThroughExecutorAndReturnsMarkedMessage()
    {
        var notificationId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.MarkNotificationRead(_user, notificationId.ToString()));

        request.OperationId.Should().Be("update_notifications");
        request.Arguments.GetRawText().Should().Contain("mark_read");
        result.Should().Be($"Marked notification {notificationId} as read.");
    }

    [Fact]
    public async Task MarkNotificationRead_NotFound_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Notification not found.");

        var result = await _tools.MarkNotificationRead(_user, Guid.NewGuid().ToString());

        result.Should().Be("Error: Notification not found.");
    }

    [Fact]
    public async Task MarkAllNotificationsRead_RoutesThroughExecutorAndReturnsMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.MarkAllNotificationsRead(_user));

        request.OperationId.Should().Be("update_notifications");
        request.Arguments.GetRawText().Should().Contain("mark_all_read");
        result.Should().Be("Marked all notifications as read.");
    }

    [Fact]
    public async Task DeleteNotification_Success_RoutesThroughExecutorAndReturnsDeletedMessage()
    {
        var notificationId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.DeleteNotification(_user, notificationId.ToString()));

        request.OperationId.Should().Be("delete_notifications");
        request.Arguments.GetRawText().Should().Contain("delete_one");
        result.Should().Be($"Deleted notification {notificationId}.");
    }

    [Fact]
    public async Task DeleteNotification_PendingConfirmation_ReturnsConfirmationPrompt()
    {
        StubExecutor(AgentOperationStatus.PendingConfirmation);

        var result = await _tools.DeleteNotification(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Confirmation required");
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
