using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
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

    [Fact]
    public async Task Handle_ThirtySevenNotifications_ReportsTotalCount()
    {
        var notifications = Enumerable.Range(0, 37)
            .Select(index => Notification.Create(UserId, $"Notice {index}", "Body"))
            .ToList();
        _notificationRepo.FindAsync(
                Arg.Any<Expression<Func<Notification, bool>>>(),
                Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(notifications.AsReadOnly());
        _notificationRepo.CountAsync(
                Arg.Any<Expression<Func<Notification, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => notifications.Count(call.ArgAt<Expression<Func<Notification, bool>>>(0).Compile()));

        var result = await _handler.Handle(new GetNotificationsQuery(UserId), CancellationToken.None);

        result.Value.TotalCount.Should().Be(37);
    }

    [Theory]
    [InlineData("/social")]
    [InlineData("/public-profile")]
    [InlineData("/u/")]
    public async Task Handle_FiftyOneUnreadRetiredNotifications_ReturnsEmptyInbox(string prefix)
    {
        using var context = CreateContext();
        var notifications = Enumerable.Range(0, 51)
            .Select(index => Notification.Create(UserId, $"Retired {index}", "Body",
                index == 0 ? prefix : $"{prefix.TrimEnd('/')}/legacy-{index}"))
            .ToList();
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();
        var handler = new GetNotificationsQueryHandler(new GenericRepository<Notification>(context));

        var result = await handler.Handle(new GetNotificationsQuery(UserId), CancellationToken.None);

        using var scope = new AssertionScope();
        result.IsSuccess.Should().BeTrue();
        result.Value.UnreadCount.Should().Be(0);
        result.Value.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
        (await context.Notifications.CountAsync()).Should().Be(51);
        notifications.Should().OnlyContain(n => !n.IsRead && !n.IsDeleted);
    }

    [Fact]
    public async Task Handle_MixedNotifications_CountsActiveUnreadRowsBeyondThePage()
    {
        using var context = CreateContext();
        var active = Enumerable.Range(0, 55)
            .Select(index => Notification.Create(UserId, $"Active {index}", "Body", "/progress"))
            .ToList();
        foreach (var notification in active.TakeLast(2))
            notification.MarkAsRead();
        var retired = new[] { "/social", "/public-profile/user", "/u/user" }
            .Select(url => Notification.Create(UserId, "Retired", "Body", url));
        var deleted = Notification.Create(UserId, "Deleted", "Body", "/chat");
        deleted.SoftDelete();
        context.Notifications.AddRange(active.Concat(retired).Append(deleted)
            .Append(Notification.Create(Guid.NewGuid(), "Other account", "Body", "/chat")));
        await context.SaveChangesAsync();
        var handler = new GetNotificationsQueryHandler(new GenericRepository<Notification>(context));

        var result = await handler.Handle(new GetNotificationsQuery(UserId), CancellationToken.None);

        using var scope = new AssertionScope();
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(50);
        result.Value.Items.Should().OnlyContain(n => n.Url == "/progress");
        result.Value.UnreadCount.Should().Be(53);
        result.Value.UnreadCount.Should().BeGreaterThan(result.Value.Items.Count(n => !n.IsRead));
        result.Value.TotalCount.Should().Be(55);
    }

    [Fact]
    public async Task Handle_OffsetAndLimit_PageOverActiveNotifications()
    {
        using var context = CreateContext();
        var urls = new[] { "/social", "/chat", "/public-profile/user", "/progress", "/u/user", "/profile", "/" };
        var notifications = urls.Select(url => Notification.Create(UserId, url, "Body", url)).ToList();
        var createdAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < notifications.Count; index++)
        {
            context.Notifications.Add(notifications[index]);
            context.Entry(notifications[index]).Property(n => n.CreatedAtUtc).CurrentValue = createdAtUtc.AddMinutes(-index);
        }
        await context.SaveChangesAsync();
        var handler = new GetNotificationsQueryHandler(new GenericRepository<Notification>(context));

        var result = await handler.Handle(new GetNotificationsQuery(UserId, Offset: 1, Limit: 2), CancellationToken.None);

        using var scope = new AssertionScope();
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(n => n.Url).Should().Equal("/progress", "/profile");
        result.Value.UnreadCount.Should().Be(4);
        result.Value.TotalCount.Should().Be(4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/")]
    [InlineData("/progress")]
    [InlineData("/u")]
    [InlineData("/user")]
    [InlineData("/Social")]
    [InlineData("/chat?return=/social")]
    public async Task Handle_ActiveUrl_KeepsNotification(string? url)
    {
        using var context = CreateContext();
        var notification = Notification.Create(UserId, "Active", "Body", url);
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();
        var handler = new GetNotificationsQueryHandler(new GenericRepository<Notification>(context));

        var result = await handler.Handle(new GetNotificationsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(notification.Id);
        result.Value.UnreadCount.Should().Be(1);
        result.Value.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_FiltersAndPaging_TranslateToPostgresWithoutConnecting()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=query_translation_only").Options);
        var translatedQueries = new List<string>();
        _notificationRepo.FindAsync(
                Arg.Any<Expression<Func<Notification, bool>>>(),
                Arg.Any<Func<IQueryable<Notification>, IQueryable<Notification>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = context.Notifications.Where(call.ArgAt<Expression<Func<Notification, bool>>>(0));
                query = call.ArgAt<Func<IQueryable<Notification>, IQueryable<Notification>>>(1)(query);
                translatedQueries.Add(query.ToQueryString());
                return Array.Empty<Notification>();
            });
        _notificationRepo.CountAsync(
                Arg.Any<Expression<Func<Notification, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                translatedQueries.Add(context.Notifications
                    .Where(call.ArgAt<Expression<Func<Notification, bool>>>(0)).ToQueryString());
                return 0;
            });

        await _handler.Handle(new GetNotificationsQuery(UserId, Offset: 1, Limit: 2), CancellationToken.None);

        translatedQueries.Should().HaveCount(3);
        translatedQueries.Should().OnlyContain(sql => !string.IsNullOrWhiteSpace(sql));
    }

    private static OrbitDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
