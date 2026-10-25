namespace Orbit.Domain.Interfaces;

public enum WnsDeliveryOutcome { Delivered, Stale, Retained }

public interface IWnsPushSender
{
    Task<WnsDeliveryOutcome> SendAsync(string channelUri, string payload, CancellationToken cancellationToken);
}
