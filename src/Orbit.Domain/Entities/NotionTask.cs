using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class NotionTask : Entity
{
    public const int MaxTitleLength = 200;
    public Guid UserId { get; private set; }
    public Guid DatabaseId { get; private set; }
    public Guid PageId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DateOnly? DueDate { get; private set; }
    public string? Status { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public DateTime NotionLastEditedAtUtc { get; private set; }
    public DateTime FetchedAtUtc { get; private set; }

    private NotionTask() { }

    public static NotionTask Create(Guid userId, Guid databaseId, Guid pageId, string title,
        DateOnly? dueDate, string? status, string url, DateTime notionLastEditedAtUtc, DateTime fetchedAtUtc)
    {
        if (userId == Guid.Empty || databaseId == Guid.Empty || pageId == Guid.Empty)
            throw new ArgumentException("A user, database and page are required.");
        ArgumentNullException.ThrowIfNull(title);
        if (title.Length > MaxTitleLength) throw new ArgumentException("The task title is too long.", nameof(title));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("An HTTPS page URL is required.", nameof(url));
        if (notionLastEditedAtUtc.Kind != DateTimeKind.Utc || fetchedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("UTC timestamps are required.");
        return new NotionTask
        {
            UserId = userId,
            DatabaseId = databaseId,
            PageId = pageId,
            Title = title,
            DueDate = dueDate,
            Status = status,
            Url = url,
            NotionLastEditedAtUtc = notionLastEditedAtUtc,
            FetchedAtUtc = fetchedAtUtc
        };
    }
}
