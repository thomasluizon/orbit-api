using FluentAssertions;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class NotificationAggregateTests
{
    [Fact]
    public async Task RevisionAndCounts_UseOneAggregateRowAndTrackNonMaximumRowChanges()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("Inbox", "aggregate@example.test").Value;
        db.Users.Add(user);
        var notifications = Enumerable.Range(0, 50).Select(index => Notification.Create(user.Id, $"Alert {index}", "Body")).ToArray();
        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync();
        var reader = new NotificationInboxAggregateReader(db);
        var handler = new GetNotificationsQueryHandler(new GenericRepository<Notification>(db), reader);
        counter.Reset();
        var first = await handler.Handle(new(user.Id, Limit: 2), default);
        counter.SelectCommandCount.Should().Be(2);
        counter.Commands.Last().Rows.Should().Be(1);
        first.Value.UnreadCount.Should().Be(50);
        var revision = first.Value.SourceRevision;
        (await reader.ReadAsync(user.Id)).Revision.Should().Be(revision);
        notifications[0].MarkAsRead();
        await db.SaveChangesAsync();
        var read = await reader.ReadAsync(user.Id);
        read.UnreadCount.Should().Be(49);
        read.Revision.Should().NotBe(revision);
        notifications[1].SoftDelete();
        await db.SaveChangesAsync();
        var deleted = await reader.ReadAsync(user.Id);
        deleted.TotalCount.Should().Be(49);
        deleted.Revision.Should().NotBe(read.Revision);
        db.Notifications.Add(Notification.Create(user.Id, "New", "Body"));
        await db.SaveChangesAsync();
        (await reader.ReadAsync(user.Id)).Revision.Should().NotBe(deleted.Revision);
    }
}
