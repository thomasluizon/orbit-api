using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class Cheer : Entity
{
    public Guid SenderId { get; private set; }
    public Guid RecipientId { get; private set; }
    public Guid? HabitId { get; private set; }
    public string? Note { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private Cheer() { }

    public static Result<Cheer> Create(Guid senderId, Guid recipientId, Guid? habitId, string? note)
    {
        if (senderId == recipientId)
            return Result.Failure<Cheer>(DomainErrors.CannotCheerSelf);

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is not null && trimmedNote.Length > DomainConstants.MaxCheerNoteLength)
            return Result.Failure<Cheer>(DomainErrors.CheerNoteTooLong.Format(DomainConstants.MaxCheerNoteLength));

        return Result.Success(new Cheer
        {
            SenderId = senderId,
            RecipientId = recipientId,
            HabitId = habitId,
            Note = trimmedNote,
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
