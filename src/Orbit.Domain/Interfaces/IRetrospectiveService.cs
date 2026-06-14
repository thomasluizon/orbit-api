using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IRetrospectiveService
{
    Task<Result<RetrospectiveNarrative>> GenerateRetrospectiveAsync(
        List<Habit> habits,
        DateOnly dateFrom,
        DateOnly dateTo,
        string period,
        string language,
        CancellationToken cancellationToken = default);
}
