using System.Text.Json.Serialization;
using System.Globalization;
using System.Text;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Tags.Queries;

namespace Orbit.Application.Chat;

public record RecordListItem(
    string Id, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Date = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsRead = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null);

public record RecordListCard(
    string Kind, int TotalCount, IReadOnlyList<RecordListItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SurfaceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor = null);

public static class RecordListCardBuilder
{
    public const int PageSize = 10;
    public const string PromptInstruction = "For a successful record list tool, write one short line without listing records. Then emit the matching directive before optional follow-ups: [[orbit:records:notifications]], [[orbit:records:tags]], [[orbit:records:templates]], or [[orbit:records:keys]].";

    public static RecordListCard BuildNotifications(GetNotificationsResponse response, Guid userId = default) =>
        BuildNotificationItems(response.Items.OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id).Take(PageSize), response.TotalCount ?? response.Items.Count, userId, 0);

    public static RecordListCard BuildNotificationPage(GetNotificationsResponse response, Guid userId, int offset) =>
        BuildNotificationItems(response.Items, response.TotalCount ?? response.Items.Count, userId, offset);

    private static RecordListCard BuildNotificationItems(IEnumerable<NotificationItemDto> items, int totalCount, Guid userId, int offset) =>
        new("notifications", totalCount,
            items
                .Select(item => new RecordListItem(item.Id.ToString(), item.Title,
                    item.Body.Length > 120 ? item.Body[..120] : item.Body,
                    item.CreatedAtUtc, item.IsRead)).ToList(), "notifications",
            NextCursor(userId, "notifications", offset, totalCount));

    public static RecordListCard BuildTags(IReadOnlyList<TagResponse> tags, Guid userId = default, int offset = 0) =>
        new("tags", tags.Count,
            tags.Skip(offset).Take(PageSize).Select(item => new RecordListItem(item.Id.ToString(), item.Name)).ToList(),
            NextCursor: NextCursor(userId, "tags", offset, tags.Count));

    public static RecordListCard BuildTemplates(IReadOnlyList<ChecklistTemplateResponse> templates, Guid userId = default, int offset = 0) =>
        new("templates", templates.Count,
            templates.Skip(offset).Take(PageSize).Select(item => new RecordListItem(
                item.Id.ToString(), item.Name, Count: item.Items.Count)).ToList(),
            NextCursor: NextCursor(userId, "templates", offset, templates.Count));

    public static RecordListCard BuildKeys(IReadOnlyList<ApiKeyResponse> keys, DateTime nowUtc, Guid userId = default, int offset = 0) =>
        new("keys", keys.Count,
            keys.Skip(offset).Take(PageSize).Select(item => new RecordListItem(
                item.Id.ToString(), item.Name,
                item.KeyPrefix,
                item.LastUsedAtUtc ?? item.CreatedAtUtc,
                State: item.IsRevoked ? "revoked" : item.ExpiresAtUtc <= nowUtc ? "expired" : "active"))
                .ToList(), "profile", NextCursor(userId, "keys", offset, keys.Count));

    private static string? NextCursor(Guid userId, string kind, int offset, int totalCount) =>
        offset + PageSize < totalCount ? RecordListCursor.Create(userId, kind, offset + PageSize) : null;
}

public static class RecordListCursor
{
    public static string Create(Guid userId, string kind, int offset)
    {
        var value = $"{userId:N}|{kind}|{offset.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryRead(string cursor, Guid userId, string kind, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 256)
            return false;

        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            var parts = value.Split('|');
            return parts.Length == 3
                && Guid.TryParseExact(parts[0], "N", out var cursorUserId)
                && cursorUserId == userId
                && parts[1] == kind
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                && offset >= RecordListCardBuilder.PageSize
                && offset % RecordListCardBuilder.PageSize == 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
