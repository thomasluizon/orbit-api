using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IProactiveCheckinMessageService
{
    Task<Result<(string Title, string Body)>> GenerateMessageAsync(
        string displayName,
        IReadOnlyList<string> offTrackHabitTitles,
        int currentStreak,
        string language,
        CancellationToken cancellationToken = default);
}
