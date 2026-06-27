using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class Tag : Entity, ITimestamped, ISoftDeletable
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Color { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private readonly List<Habit> _habits = [];
    public IReadOnlyCollection<Habit> Habits => _habits.AsReadOnly();

    private Tag() { }

    public static Result<Tag> Create(Guid userId, string name, string color)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Tag>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Tag>(DomainErrors.TagNameRequired);

        if (name.Trim().Length > 50)
            return Result.Failure<Tag>(DomainErrors.TagNameTooLong);

        if (string.IsNullOrWhiteSpace(color))
            return Result.Failure<Tag>(DomainErrors.TagColorRequired);

        var trimmed = name.Trim();
        return Result.Success(new Tag
        {
            UserId = userId,
            Name = char.ToUpper(trimmed[0]) + trimmed[1..].ToLower(),
            Color = color.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public Result Update(string name, string color)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(DomainErrors.TagNameRequired);

        if (name.Trim().Length > 50)
            return Result.Failure(DomainErrors.TagNameTooLong);

        if (string.IsNullOrWhiteSpace(color))
            return Result.Failure(DomainErrors.TagColorRequired);

        var trimmed = name.Trim();
        Name = char.ToUpper(trimmed[0]) + trimmed[1..].ToLower();
        Color = color.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }
}
