using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class Tag : Entity
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public string Color { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    public ICollection<Habit> Habits { get; private set; } = [];

    private Tag() { }

    public static Result<Tag> Create(Guid userId, string name, string color)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Tag>("User ID is required.");

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Tag>("Tag name is required.");

        if (string.IsNullOrWhiteSpace(color) || !IsValidHexColor(color))
            return Result.Failure<Tag>("Tag color must be a valid hex color (e.g., #FF5733).");

        return Result.Success(new Tag
        {
            UserId = userId,
            Name = name.Trim(),
            Color = color.Trim().ToUpperInvariant(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static bool IsValidHexColor(string color)
    {
        var trimmed = color.Trim();
        if (trimmed.Length != 7 || trimmed[0] != '#') return false;
        return trimmed[1..].All(c => "0123456789ABCDEFabcdef".Contains(c));
    }
}
