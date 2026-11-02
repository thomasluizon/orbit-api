namespace Orbit.Domain.Interfaces;

public interface IPushNotificationService
{
    Task<IReadOnlySet<int>> TrySendManyAsync(IReadOnlyList<Orbit.Domain.Models.PushDelivery> deliveries, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<int>> TrySendManyAsync(IReadOnlyList<Orbit.Domain.Models.PushDelivery> deliveries,
        Func<int, Task> onDelivered, CancellationToken cancellationToken = default);

    Task SendManyAsync(IReadOnlyList<Orbit.Domain.Models.PushDelivery> deliveries, CancellationToken cancellationToken = default);

    Task SendToUserAsync(Guid userId, string title, string body, string? url = null, CancellationToken cancellationToken = default);

    Task<bool> TrySendReminderToUserAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string> data,
        string? url = null, CancellationToken cancellationToken = default);

    Task<bool> TrySendToUserAsync(Guid userId, string title, string body, string? url = null, CancellationToken cancellationToken = default);
}
