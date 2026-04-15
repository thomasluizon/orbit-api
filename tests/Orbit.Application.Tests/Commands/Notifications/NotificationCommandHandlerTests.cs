using FluentAssertions;
using NSubstitute;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Notifications;

public class MarkNotificationReadCommandHandlerTests
{
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MarkNotificationReadCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid NotificationId = Guid.NewGuid();

    public MarkNotificationReadCommandHandlerTests()
    {
        _handler = new MarkNotificationReadCommandHandler(_notificationRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_NotificationFound_MarksAsRead()
    {
        var notification = Notification.Create(UserId, "Title", "Body");

        _notificationRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(notification);

        var command = new MarkNotificationReadCommand(UserId, NotificationId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        notification.IsRead.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotificationNotFound_ReturnsFailure()
    {
        _notificationRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Notification?)null);

        var command = new MarkNotificationReadCommand(UserId, NotificationId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Notification not found");
        result.ErrorCode.Should().Be("NOTIFICATION_NOT_FOUND");
    }
}

public class MarkAllNotificationsReadCommandHandlerTests
{
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MarkAllNotificationsReadCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public MarkAllNotificationsReadCommandHandlerTests()
    {
        _handler = new MarkAllNotificationsReadCommandHandler(_notificationRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_UnreadNotifications_MarksAllAsRead()
    {
        var notifications = new List<Notification>
        {
            Notification.Create(UserId, "Title 1", "Body 1"),
            Notification.Create(UserId, "Title 2", "Body 2")
        };

        _notificationRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(notifications.AsReadOnly());

        var command = new MarkAllNotificationsReadCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        notifications.Should().AllSatisfy(n => n.IsRead.Should().BeTrue());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoUnreadNotifications_StillSucceeds()
    {
        _notificationRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Notification>().AsReadOnly());

        var command = new MarkAllNotificationsReadCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}

public class DeleteNotificationCommandHandlerTests
{
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DeleteNotificationCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid NotificationId = Guid.NewGuid();

    public DeleteNotificationCommandHandlerTests()
    {
        _handler = new DeleteNotificationCommandHandler(_notificationRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_NotificationFound_DeletesIt()
    {
        var notification = Notification.Create(UserId, "Title", "Body");

        _notificationRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(notification);

        var command = new DeleteNotificationCommand(UserId, NotificationId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _notificationRepo.Received(1).Remove(notification);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotificationNotFound_StillReturnsSuccess()
    {
        _notificationRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Notification?)null);

        var command = new DeleteNotificationCommand(UserId, NotificationId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _notificationRepo.DidNotReceive().Remove(Arg.Any<Notification>());
    }
}

public class DeleteAllNotificationsCommandHandlerTests
{
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DeleteAllNotificationsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public DeleteAllNotificationsCommandHandlerTests()
    {
        _handler = new DeleteAllNotificationsCommandHandler(_notificationRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_WithNotifications_DeletesAll()
    {
        var notifications = new List<Notification>
        {
            Notification.Create(UserId, "Title 1", "Body 1"),
            Notification.Create(UserId, "Title 2", "Body 2")
        };

        _notificationRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(notifications.AsReadOnly());

        var command = new DeleteAllNotificationsCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _notificationRepo.Received(1).RemoveRange(Arg.Any<IReadOnlyList<Notification>>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoNotifications_StillSucceeds()
    {
        _notificationRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Notification, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Notification>().AsReadOnly());

        var command = new DeleteAllNotificationsCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

public class SubscribePushCommandHandlerTests
{
    private readonly IGenericRepository<PushSubscription> _pushSubRepo = Substitute.For<IGenericRepository<PushSubscription>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SubscribePushCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SubscribePushCommandHandlerTests()
    {
        _handler = new SubscribePushCommandHandler(_pushSubRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_NewSubscription_CreatesIt()
    {
        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((PushSubscription?)null);

        _pushSubRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<PushSubscription>().AsReadOnly());

        var command = new SubscribePushCommand(UserId, "https://push.example.com/endpoint", "p256dh_key", "auth_key");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.Received(1).AddAsync(Arg.Any<PushSubscription>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingEndpointSameUser_NoOp()
    {
        var existing = PushSubscription.Create(UserId, "https://push.example.com/endpoint", "p256dh", "auth").Value;

        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(existing);

        var command = new SubscribePushCommand(UserId, "https://push.example.com/endpoint", "p256dh", "auth");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.DidNotReceive().AddAsync(Arg.Any<PushSubscription>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingEndpointDifferentUser_RejectsToPreventHijack()
    {
        // Push subscription endpoints are unique per browser/device. If a different user already
        // owns this endpoint, the request is most likely an attempt to hijack notifications --
        // legitimate browser re-registration produces a NEW endpoint URL. The handler now
        // rejects rather than silently replacing.
        var otherUserId = Guid.NewGuid();
        var existing = PushSubscription.Create(otherUserId, "https://push.example.com/endpoint", "p256dh", "auth").Value;

        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(existing);

        var command = new SubscribePushCommand(UserId, "https://push.example.com/endpoint", "new_p256dh", "new_auth");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        _pushSubRepo.DidNotReceive().Remove(Arg.Any<PushSubscription>());
        await _pushSubRepo.DidNotReceive().AddAsync(Arg.Any<PushSubscription>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NonHttpsEndpoint_RejectsAsSsrfDefense()
    {
        var command = new SubscribePushCommand(UserId, "http://push.example.com/endpoint", "p256dh", "auth");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _pushSubRepo.DidNotReceive().AddAsync(Arg.Any<PushSubscription>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FcmEndpoint_AcceptsRawTokenWithoutHttpsCheck()
    {
        // FCM stores a raw Firebase registration token in Endpoint (not a URL). The
        // PushNotificationService passes it as Message.Token to the Firebase Admin SDK,
        // which addresses Google's servers internally. The https-only validation must
        // not apply here, otherwise no native (mobile) subscription can register.
        _pushSubRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((PushSubscription?)null);

        _pushSubRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<PushSubscription, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<PushSubscription>().AsReadOnly());

        var command = new SubscribePushCommand(UserId, "fcm-token-abc123", "fcm", "ignored");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pushSubRepo.Received(1).AddAsync(Arg.Any<PushSubscription>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FcmEndpoint_RejectsEmptyToken()
    {
        var command = new SubscribePushCommand(UserId, "   ", "fcm", "ignored");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _pushSubRepo.DidNotReceive().AddAsync(Arg.Any<PushSubscription>(), Arg.Any<CancellationToken>());
    }
}
