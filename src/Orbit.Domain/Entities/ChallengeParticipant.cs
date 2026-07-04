using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ChallengeParticipant : Entity
{
    public Guid ChallengeId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime JoinedAtUtc { get; private set; }
    public DateTime? LeftAtUtc { get; private set; }

    public bool IsActive => LeftAtUtc is null;

    private readonly List<ChallengeParticipantHabit> _linkedHabits = [];
    public IReadOnlyCollection<ChallengeParticipantHabit> LinkedHabits => _linkedHabits.AsReadOnly();

    private ChallengeParticipant() { }

    internal static ChallengeParticipant Create(Guid challengeId, Guid userId)
    {
        return new ChallengeParticipant
        {
            ChallengeId = challengeId,
            UserId = userId,
            JoinedAtUtc = DateTime.UtcNow
        };
    }

    internal void LinkHabit(Guid habitId)
    {
        if (_linkedHabits.Exists(h => h.HabitId == habitId))
            return;

        _linkedHabits.Add(ChallengeParticipantHabit.Create(Id, habitId));
    }

    internal void ReplaceLinkedHabits(IReadOnlyList<Guid> habitIds)
    {
        _linkedHabits.Clear();
        foreach (var habitId in habitIds)
            LinkHabit(habitId);
    }

    internal void Leave()
    {
        LeftAtUtc ??= DateTime.UtcNow;
    }
}
