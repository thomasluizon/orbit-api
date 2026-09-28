namespace Orbit.Domain.Interfaces;

public interface ISesEventProcessor
{
    Task<bool> ProcessAsync(string payload, CancellationToken cancellationToken);
}
