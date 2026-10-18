namespace Orbit.Domain.Entities;

public class AppFeatureFlag
{
    public string Key { get; private set; } = null!;
    public bool Enabled { get; private set; }
    public string? PlanRequirement { get; private set; }
    public string? Description { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private AppFeatureFlag() { }

    public static AppFeatureFlag Create(string key, bool enabled, string? planRequirement = null, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 100 || key != key.Trim())
            throw new ArgumentException("Flag keys must be trimmed and at most 100 characters.", nameof(key));
        if (description is { Length: > 500 } || description?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Descriptions must be one line of at most 500 characters.", nameof(description));
        if (planRequirement is { Length: > 50 })
            throw new ArgumentException("Plan requirements must be at most 50 characters.", nameof(planRequirement));

        return new AppFeatureFlag
        {
            Key = key,
            Enabled = enabled,
            PlanRequirement = planRequirement,
            Description = description,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
