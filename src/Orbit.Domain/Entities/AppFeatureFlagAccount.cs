namespace Orbit.Domain.Entities;

public class AppFeatureFlagAccount
{
    public string FlagKey { get; private set; } = null!;
    public Guid UserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private AppFeatureFlagAccount() { }

    public static AppFeatureFlagAccount Create(string flagKey, Guid userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        if (flagKey.Length > 100 || flagKey != flagKey.Trim())
            throw new ArgumentException("Flag keys must be trimmed and at most 100 characters.", nameof(flagKey));
        if (userId == Guid.Empty)
            throw new ArgumentException("An account is required.", nameof(userId));

        return new AppFeatureFlagAccount { FlagKey = flagKey, UserId = userId, CreatedAtUtc = DateTime.UtcNow };
    }
}
