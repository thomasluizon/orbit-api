using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class Tag : Entity
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Color { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    private readonly List<Habit> _habits = [];
    public IReadOnlyCollection<Habit> Habits => _habits.AsReadOnly();

    private Tag() { }

    public static Result<Tag> Create(Guid userId, string name, string color)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Tag>("User ID is required.");

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Tag>("Tag name is required.");

        if (name.Trim().Length > 50)
            return Result.Failure<Tag>("Tag name must be 50 characters or less.");

        if (string.IsNullOrWhiteSpace(color))
            return Result.Failure<Tag>("Tag color is required.");

        var trimmed = name.Trim();
        return Result.Success(new Tag
        {
            UserId = userId,
            Name = char.ToUpper(trimmed[0]) + trimmed[1..].ToLower(),
            Color = color.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result Update(string name, string color)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Tag name is required.");

        if (name.Trim().Length > 50)
            return Result.Failure("Tag name must be 50 characters or less.");

        if (string.IsNullOrWhiteSpace(color))
            return Result.Failure("Tag color is required.");

        var trimmed = name.Trim();
        Name = char.ToUpper(trimmed[0]) + trimmed[1..].ToLower();
        Color = color.Trim();
        return Result.Success();
    }
}
