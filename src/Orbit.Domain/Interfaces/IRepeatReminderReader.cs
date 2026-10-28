namespace Orbit.Domain.Interfaces;

public interface IRepeatReminderReader
{
    Task<IReadOnlyDictionary<Guid, DateTime?>> GetNextAsync(
        Guid userId, IReadOnlyCollection<Guid> habitIds, CancellationToken cancellationToken);
}
