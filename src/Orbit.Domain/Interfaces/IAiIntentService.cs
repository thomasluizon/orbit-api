using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IAiIntentService
{
    Task<Result<AiActionPlan>> InterpretAsync(
        string userMessage,
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<UserFact> userFacts,
        byte[]? imageData = null,
        string? imageMimeType = null,
        IReadOnlyList<RoutinePattern>? routinePatterns = null,
        IReadOnlyList<Tag>? userTags = null,
        DateOnly? userToday = null,
        CancellationToken cancellationToken = default);
}
