using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ChecklistTemplate : Entity, ITimestamped
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public IReadOnlyList<string> Items { get; private set; } = [];
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    private ChecklistTemplate() { }

    public static Result<ChecklistTemplate> Create(Guid userId, string name, IReadOnlyList<string> items)
    {
        if (userId == Guid.Empty)
            return Result.Failure<ChecklistTemplate>("User ID is required.");

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<ChecklistTemplate>("Template name is required.");

        if (name.Trim().Length > 100)
            return Result.Failure<ChecklistTemplate>("Template name must be 100 characters or less.");

        if (items.Count == 0)
            return Result.Failure<ChecklistTemplate>("At least one item is required.");

        return Result.Success(new ChecklistTemplate
        {
            UserId = userId,
            Name = name.Trim(),
            Items = items.Select(i => i.Trim()).Where(i => !string.IsNullOrEmpty(i)).ToList(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    public Result Update(string name, IReadOnlyList<string> items)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Template name is required.");

        if (name.Trim().Length > 100)
            return Result.Failure("Template name must be 100 characters or less.");

        if (items.Count == 0)
            return Result.Failure("At least one item is required.");

        Name = name.Trim();
        Items = items.Select(i => i.Trim()).Where(i => !string.IsNullOrEmpty(i)).ToList();
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }
}
