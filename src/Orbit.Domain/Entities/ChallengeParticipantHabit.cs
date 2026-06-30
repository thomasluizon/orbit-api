using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ChallengeParticipantHabit : Entity
{
    public Guid ChallengeParticipantId { get; private set; }
    public Guid HabitId { get; private set; }

    private ChallengeParticipantHabit() { }

    internal static ChallengeParticipantHabit Create(Guid challengeParticipantId, Guid habitId)
    {
        return new ChallengeParticipantHabit
        {
            ChallengeParticipantId = challengeParticipantId,
            HabitId = habitId
        };
    }
}
