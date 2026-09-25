using System.Text.Json.Serialization;
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null);

public record RecordListCard(
    string Kind, int TotalCount, IReadOnlyList<RecordListItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SurfaceId = null);

public static class RecordListCardBuilder
{
    public const string PromptInstruction = "For a successful record list tool, write one short line without listing records. Then emit the matching directive before optional follow-ups: [[orbit:records:notifications]], [[orbit:records:tags]], [[orbit:records:templates]], or [[orbit:records:keys]].";

    public static RecordListCard BuildNotifications(GetNotificationsResponse response) =>
        new("notifications", response.TotalCount ?? response.Items.Count,
            response.Items.OrderByDescending(item => item.CreatedAtUtc).Take(10)
                .Select(item => new RecordListItem(item.Id.ToString(), item.Title,
                    item.Body.Length > 120 ? item.Body[..120] : item.Body,
                    item.CreatedAtUtc, item.IsRead)).ToList(), "notifications");

    public static RecordListCard BuildTags(IReadOnlyList<TagResponse> tags) =>
        new("tags", tags.Count,
            tags.Take(10).Select(item => new RecordListItem(item.Id.ToString(), item.Name)).ToList());

    public static RecordListCard BuildTemplates(IReadOnlyList<ChecklistTemplateResponse> templates) =>
        new("templates", templates.Count,
            templates.Take(10).Select(item => new RecordListItem(
                item.Id.ToString(), item.Name, Count: item.Items.Count)).ToList());

    public static RecordListCard BuildKeys(IReadOnlyList<ApiKeyResponse> keys, DateTime nowUtc) =>
        new("keys", keys.Count,
            keys.Take(10).Select(item => new RecordListItem(
                item.Id.ToString(), item.Name,
                item.KeyPrefix + (item.IsRevoked ? " (revoked)"
                    : item.ExpiresAtUtc <= nowUtc ? " (expired)" : " (active)"),
                item.LastUsedAtUtc ?? item.CreatedAtUtc)).ToList(), "profile");
}
