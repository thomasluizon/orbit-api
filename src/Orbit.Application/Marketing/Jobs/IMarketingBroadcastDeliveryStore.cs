namespace Orbit.Application.Marketing.Jobs;

public interface IMarketingBroadcastDeliveryStore
{
    Task<IReadOnlySet<Guid>> GetDeliveredRecipientIdsAsync(Guid broadcastId, IReadOnlyCollection<Guid> recipientIds,
        CancellationToken cancellationToken);
    Task RecordDeliveredAsync(Guid broadcastId, Guid recipientId, DateTime deliveredAtUtc, CancellationToken cancellationToken);
}
