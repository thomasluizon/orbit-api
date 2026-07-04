using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public record CreateChallengeParams(
    Guid CreatorId,
    ChallengeType Type,
    string Title,
    string? Description,
    int? TargetCount,
    DateOnly PeriodStartUtc,
    DateOnly? PeriodEndUtc,
    string JoinCode);

public class Challenge : Entity, ITimestamped, ISoftDeletable
{
    public Guid CreatorId { get; private set; }
    public ChallengeType Type { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public ChallengeStatus Status { get; private set; }
    public int? TargetCount { get; private set; }
    public DateOnly PeriodStartUtc { get; private set; }
    public DateOnly? PeriodEndUtc { get; private set; }
    public string JoinCode { get; private set; } = null!;
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private readonly List<ChallengeParticipant> _participants = [];
    public IReadOnlyCollection<ChallengeParticipant> Participants => _participants.AsReadOnly();

    public IReadOnlyList<ChallengeParticipant> GetActiveParticipants() => _participants.Where(p => p.IsActive).ToList();

    private Challenge() { }

    public static Result<Challenge> Create(CreateChallengeParams p)
    {
        if (p.CreatorId == Guid.Empty)
            return Result.Failure<Challenge>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(p.Title))
            return Result.Failure<Challenge>(DomainErrors.TitleRequired);

        if (string.IsNullOrWhiteSpace(p.JoinCode))
            return Result.Failure<Challenge>(DomainErrors.ChallengeJoinCodeRequired);

        var typeValidation = ChallengeInvariants.ValidateTypeAndTarget(p.Type, p.TargetCount);
        if (typeValidation is not null)
            return Result.Failure<Challenge>(typeValidation);

        var periodValidation = ChallengeInvariants.ValidatePeriod(p.PeriodStartUtc, p.PeriodEndUtc);
        if (periodValidation is not null)
            return Result.Failure<Challenge>(periodValidation);

        return Result.Success(new Challenge
        {
            CreatorId = p.CreatorId,
            Type = p.Type,
            Title = p.Title.Trim(),
            Description = p.Description?.Trim(),
            Status = ChallengeStatus.Active,
            TargetCount = p.TargetCount,
            PeriodStartUtc = p.PeriodStartUtc,
            PeriodEndUtc = p.PeriodEndUtc,
            JoinCode = p.JoinCode,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Adds a participant and links their own habits, whose logs feed the shared progress. The caller
    /// is responsible for enforcing participation policy (status, cap, friendship, no duplicate active
    /// membership) before invoking this — the aggregate owns only how participants are stored.
    /// </summary>
    public ChallengeParticipant AddParticipant(Guid userId, IReadOnlyList<Guid> habitIds)
    {
        var participant = ChallengeParticipant.Create(Id, userId);
        foreach (var habitId in habitIds)
            participant.LinkHabit(habitId);

        _participants.Add(participant);
        UpdatedAtUtc = DateTime.UtcNow;
        return participant;
    }

    public bool TryLeave(Guid userId)
    {
        var participant = _participants.Find(p => p.UserId == userId && p.IsActive);
        if (participant is null)
            return false;

        participant.Leave();
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Replaces an active participant's own linked-habit set, whose logs feed the shared progress.
    /// Returns false when the user is not an active participant so the caller can surface a uniform
    /// not-a-participant failure.
    /// </summary>
    public bool TrySetParticipantHabits(Guid userId, IReadOnlyList<Guid> habitIds)
    {
        var participant = _participants.Find(p => p.UserId == userId && p.IsActive);
        if (participant is null)
            return false;

        participant.ReplaceLinkedHabits(habitIds);
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Transitions an active challenge to completed, returning true only on the Active to Completed
    /// transition so callers fire the Mission Accomplished award exactly once.
    /// </summary>
    public bool MarkCompleted()
    {
        if (Status != ChallengeStatus.Active)
            return false;

        Status = ChallengeStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
