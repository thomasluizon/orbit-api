using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IRoutineAnalysisService
{
    Task<Result<RoutineAnalysis>> AnalyzeRoutinesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<Result<ConflictWarning?>> DetectConflictsAsync(
        Guid userId,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        IReadOnlyList<DayOfWeek>? days,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<TimeSlotSuggestion>>> SuggestTimeSlotsAsync(
        Guid userId,
        string habitTitle,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        CancellationToken cancellationToken = default);
}
