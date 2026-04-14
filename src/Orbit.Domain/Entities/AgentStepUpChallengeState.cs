using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class AgentStepUpChallengeState : Entity
{
    public Guid UserId { get; private set; }
    public Guid PendingOperationId { get; private set; }
    public string CodeHash { get; private set; } = null!;
    public int AttemptCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? VerifiedAtUtc { get; private set; }

    private AgentStepUpChallengeState()
    {
    }

    public static AgentStepUpChallengeState Create(
        Guid userId,
        Guid pendingOperationId,
        string codeHash,
        DateTime expiresAtUtc)
    {
        return new AgentStepUpChallengeState
        {
            UserId = userId,
            PendingOperationId = pendingOperationId,
            CodeHash = codeHash,
            AttemptCount = 0,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;

    public bool CanVerify(int maxAttempts, DateTime utcNow)
    {
        return !VerifiedAtUtc.HasValue && !IsExpired(utcNow) && AttemptCount < maxAttempts;
    }

    public void RecordFailedAttempt()
    {
        AttemptCount++;
    }

    public void MarkVerified()
    {
        VerifiedAtUtc = DateTime.UtcNow;
    }
}
