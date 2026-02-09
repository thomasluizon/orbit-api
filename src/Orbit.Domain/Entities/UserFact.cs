using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class UserFact : Entity
{
    public Guid UserId { get; private set; }
    public string FactText { get; private set; } = null!;
    public string? Category { get; private set; }
    public DateTime ExtractedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private UserFact() { }

    public static Result<UserFact> Create(Guid userId, string factText, string? category)
    {
        if (string.IsNullOrWhiteSpace(factText))
            return Result.Failure<UserFact>("Fact text is required");

        var trimmedText = factText.Trim();

        if (trimmedText.Length > 500)
            return Result.Failure<UserFact>("Fact text cannot exceed 500 characters");

        // Basic prompt injection detection
        var lowerText = trimmedText.ToLowerInvariant();
        if (lowerText.Contains("ignore") ||
            lowerText.Contains("system:") ||
            lowerText.Contains("you must") ||
            lowerText.Contains("instruction:"))
        {
            return Result.Failure<UserFact>("Fact text contains suspicious patterns");
        }

        return Result.Success(new UserFact
        {
            UserId = userId,
            FactText = trimmedText,
            Category = category,
            ExtractedAtUtc = DateTime.UtcNow
        });
    }

    public void Update(string newFactText)
    {
        if (string.IsNullOrWhiteSpace(newFactText))
            throw new ArgumentException("Fact text cannot be empty", nameof(newFactText));

        FactText = newFactText.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
    }
}
