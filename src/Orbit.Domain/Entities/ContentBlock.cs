using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ContentBlock : Entity, ITimestamped
{
    public string Key { get; private set; } = null!;
    public string Locale { get; private set; } = null!;
    public string Content { get; private set; } = null!;
    public string Category { get; private set; } = null!;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    private ContentBlock() { }

    public static ContentBlock Create(string key, string locale, string content, string category)
    {
        return new ContentBlock
        {
            Key = key,
            Locale = locale,
            Content = content,
            Category = category,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public void Update(string content)
    {
        Content = content;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
