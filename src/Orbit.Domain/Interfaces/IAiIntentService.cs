using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IAiIntentService
{
    Task<Result<AiActionPlan>> InterpretAsync(
        string userMessage,
        IReadOnlyList<Habit> activeHabits,
        IReadOnlyList<Tag> userTags,
        CancellationToken cancellationToken = default);
}
