using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IDateInterpretationService
{
    Task<Result<DateOnly?>> InterpretAsync(Guid userId, DateInterpretationInput input,
        DateOnly referenceDate, TimeZoneInfo timeZone, CancellationToken cancellationToken);
}
