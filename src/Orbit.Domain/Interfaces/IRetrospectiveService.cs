using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IRetrospectiveService
{
    Task<Result<string>> GenerateRetrospectiveAsync(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language,
        CancellationToken cancellationToken = default);
}
