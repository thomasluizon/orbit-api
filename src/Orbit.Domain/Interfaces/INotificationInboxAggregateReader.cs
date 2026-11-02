namespace Orbit.Domain.Interfaces;

public sealed record NotificationInboxAggregate(int UnreadCount, int TotalCount, string Revision);

public interface INotificationInboxAggregateReader
{
    Task<NotificationInboxAggregate> ReadAsync(Guid userId, CancellationToken cancellationToken = default);
}
