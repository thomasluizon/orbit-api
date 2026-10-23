namespace Orbit.Domain.Interfaces;

public enum ApnsDeliveryOutcome { Delivered, Stale, Retained }

public interface IApnsPushSender
{
    Task<ApnsDeliveryOutcome> SendAsync(string deviceToken, string title, string body, string? url, CancellationToken cancellationToken);
}
