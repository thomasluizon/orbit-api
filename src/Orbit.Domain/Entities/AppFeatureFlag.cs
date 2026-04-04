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
