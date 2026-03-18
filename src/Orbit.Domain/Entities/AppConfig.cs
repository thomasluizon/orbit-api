namespace Orbit.Domain.Entities;

public class AppConfig
{
    public string Key { get; private set; } = null!;
    public string Value { get; private set; } = null!;
    public string? Description { get; private set; }

    private AppConfig() { }

    public static AppConfig Create(string key, string value, string? description = null)
    {
        return new AppConfig
        {
            Key = key,
            Value = value,
            Description = description
        };
    }

    public void SetValue(string value) => Value = value;
}
