using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class AccountabilityCheckIn : Entity
{
    public Guid PairId { get; private set; }
    public Guid UserId { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Note { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private AccountabilityCheckIn() { }

    public static Result<AccountabilityCheckIn> Create(Guid pairId, Guid userId, DateOnly date, string? note)
    {
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is not null && trimmedNote.Length > DomainConstants.MaxAccountabilityNoteLength)
            return Result.Failure<AccountabilityCheckIn>(
                DomainErrors.AccountabilityNoteTooLong.Format(DomainConstants.MaxAccountabilityNoteLength));

        return Result.Success(new AccountabilityCheckIn
        {
            PairId = pairId,
            UserId = userId,
            Date = date,
            Note = trimmedNote,
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
