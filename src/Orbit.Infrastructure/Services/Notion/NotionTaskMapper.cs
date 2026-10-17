using System.Globalization;
using System.Text.Json;
using Orbit.Application.Common;
using Orbit.Application.Connections;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Services.Notion;

internal sealed class NotionTaskMapper(JsonElement source)
{
    public Guid DataSourceId { get; } = source.GetProperty("id").GetGuid();
    public NotionDatabase Database { get; } = new(source.GetProperty("parent").GetProperty("database_id").GetGuid(),
        ReadTitle(source.GetProperty("title")));
    private readonly JsonElement[] _properties = source.GetProperty("properties").EnumerateObject().Select(p => p.Value).ToArray();

    public NotionTask? Map(JsonElement page, Guid userId, DateOnly today, TimeZoneInfo userTimeZone, DateTime fetchedAtUtc)
    {
        var dateProperty = _properties.FirstOrDefault(p => p.GetProperty("type").GetString() == "date");
        var statusProperty = _properties.FirstOrDefault(p => p.GetProperty("type").GetString() == "status");
        if (statusProperty.ValueKind == JsonValueKind.Undefined)
            statusProperty = _properties.FirstOrDefault(p => p.GetProperty("type").GetString() == "checkbox");

        var properties = page.GetProperty("properties").EnumerateObject().Select(p => p.Value).ToArray();
        var title = properties.First(p => p.GetProperty("type").GetString() == "title");
        var date = FindValue(properties, dateProperty);
        var dueDate = date.ValueKind == JsonValueKind.Undefined ? null : ReadDate(date.GetProperty("date"), userTimeZone);
        var status = FindValue(properties, statusProperty);
        var (label, isOpen) = ReadStatus(status, statusProperty);
        var titlesOnly = dateProperty.ValueKind == JsonValueKind.Undefined && statusProperty.ValueKind == JsonValueKind.Undefined;
        if (!titlesOnly && !isOpen && !(dueDate >= today.AddDays(-1) && dueDate <= today.AddDays(14))) return null;
        return NotionTask.Create(userId, Database.Id, page.GetProperty("id").GetGuid(), ReadTitle(title.GetProperty("title")),
            dueDate, label, page.GetProperty("url").GetString()!, page.GetProperty("last_edited_time").GetDateTime().ToUniversalTime(), fetchedAtUtc);
    }

    private static JsonElement FindValue(JsonElement[] values, JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Undefined ? default
            : values.FirstOrDefault(p => p.GetProperty("id").GetString() == schema.GetProperty("id").GetString());

    private static (string? Label, bool Open) ReadStatus(JsonElement value, JsonElement schema)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return (null, false);
        if (value.GetProperty("type").GetString() == "checkbox")
        {
            var done = value.GetProperty("checkbox").GetBoolean();
            return (done ? "Done" : "Open", !done);
        }
        var status = value.GetProperty("status");
        if (status.ValueKind == JsonValueKind.Null) return (null, false);
        var optionId = status.GetProperty("id").GetString();
        var open = schema.GetProperty("status").GetProperty("groups").EnumerateArray()
            .Where(g => g.GetProperty("name").GetString() is "To-do" or "In progress")
            .Any(g => g.GetProperty("option_ids").EnumerateArray().Any(id => id.GetString() == optionId));
        return (status.GetProperty("name").GetString(), open);
    }

    private static DateOnly? ReadDate(JsonElement date, TimeZoneInfo userTimeZone)
    {
        if (date.ValueKind == JsonValueKind.Null) return null;
        var start = date.GetProperty("start").GetString()!;
        if (DateOnly.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return day;
        DateTimeOffset instant;
        if (date.TryGetProperty("time_zone", out var zone) && zone.ValueKind == JsonValueKind.String)
        {
            var local = DateTime.SpecifyKind(DateTime.Parse(start, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
            instant = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneHelper.FindTimeZone(zone.GetString()));
        }
        else instant = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, userTimeZone).DateTime);
    }

    internal static string ReadTitle(JsonElement richText)
    {
        var title = string.Concat(richText.EnumerateArray().Select(t => t.GetProperty("plain_text").GetString()));
        if (title.Length <= NotionTask.MaxTitleLength) return title;
        var length = char.IsHighSurrogate(title[NotionTask.MaxTitleLength - 1]) ? NotionTask.MaxTitleLength - 1 : NotionTask.MaxTitleLength;
        return title[..length];
    }
}
