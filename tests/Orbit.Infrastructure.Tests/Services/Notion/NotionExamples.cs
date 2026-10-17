using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Infrastructure.Tests.Services.Notion;

internal static class NotionExamples
{
    internal static readonly Guid SourceId = Guid.Parse("2f26ee68-df30-4251-aad4-8ddc420cba3d");
    internal static readonly Guid DatabaseId = Guid.Parse("842a0286-cef0-46a8-abba-eac4c8ca644e");

    internal static JsonObject Read(string file) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Notion", file + ".json")))!.AsObject();

    internal static JsonElement Source(bool date = true, bool status = true, bool checkbox = false)
    {
        var properties = Read("title-schema");
        if (date) Merge(properties, Read("date-schema"));
        if (status) Merge(properties, Read("status-schema"));
        if (checkbox) Merge(properties, Read("checkbox-schema"));
        return JsonSerializer.SerializeToElement(new
        {
            @object = "data_source",
            id = SourceId,
            parent = new { type = "database_id", database_id = DatabaseId },
            title = Read("title-page-value")["Name"]!["title"],
            properties
        });
    }

    internal static JsonObject Page(string? due = "2026-09-15", bool status = true, bool checkbox = false)
    {
        var page = Read("page");
        var properties = Read("title-page-value");
        var date = Read("date-page-value");
        if (due is null) date["Due date"]!["date"] = null;
        else date["Due date"]!["date"]!["start"] = due;
        Merge(properties, date);
        if (status) Merge(properties, Read("status-page-value"));
        if (checkbox) Merge(properties, Read("checkbox-page-value"));
        page["properties"] = properties;
        return page;
    }

    internal static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);

    private static void Merge(JsonObject destination, JsonObject source)
    {
        foreach (var property in source) destination[property.Key] = property.Value?.DeepClone();
    }
}
