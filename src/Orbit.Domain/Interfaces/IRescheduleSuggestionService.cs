using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IRescheduleSuggestionService
{
    /// <summary>
    /// Generates a realistic reschedule proposal for an overdue habit. The habit's
    /// <see cref="Habit.Logs"/> are expected to be windowed to the recent history the prompt reasons
    /// about. Raw model output is validated and clamped before returning, so the result is always a
    /// schedule the existing habit-update path accepts.
    /// </summary>
    Task<Result<RescheduleSuggestion>> GenerateAsync(
        Habit habit,
        DateOnly userToday,
        string language,
        CancellationToken cancellationToken = default);
}
