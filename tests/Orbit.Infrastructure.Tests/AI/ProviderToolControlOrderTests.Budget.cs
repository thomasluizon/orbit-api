#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.AI;

public sealed partial class ProviderToolControlOrderTests
{
    // https://github.com/thomasluizon/orbit-tickets/issues/1674: this order measures gpt-6-luna's limit between S=24,035 and S=24,737; 21,000 reserves headroom.
    private const int SchemaStringBudget = 21_000;

    [Fact]
    public async Task AllProviderCatalogs_StayWithinSchemaStringBudget()
    {
        var violations = new List<string>();
        foreach (var client in new[] { "legacy", "composition", "offers" })
            foreach (var phase in new[] { "initial", "post-read", "draft-revision", "post-read-draft" })
            {
                var (registry, tools) = await BuildProviderCatalogAsync(client, phase);
                var sizes = tools.Select(pair =>
                {
                    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(pair.Value).ToString());
                    return (Name: pair.Key, Size: SchemaStringLength(wire.RootElement.GetProperty("parameters")));
                }).ToArray();
                var size = sizes.Sum(tool => tool.Size);
                output.WriteLine($"{client}/{phase}: S={size}; tools={tools.Count}");
                if (size > SchemaStringBudget) violations.Add($"{client}/{phase}: S={size}");
                if (client == "offers" && phase == "post-read")
                    foreach (var tool in sizes.OrderByDescending(tool => tool.Size).Take(5))
                        output.WriteLine($"  {tool.Name}: S={tool.Size}");
                var names = registry.GetAll().Where(tool => tool.Name != "ask").Select(tool => tool.Name);
                if (client != "legacy" && phase != "initial") names = names.Append("ask");
                tools.Keys.Should().BeEquivalentTo(names);
            }
        violations.Should().BeEmpty($"every initial and continuation catalog must have S <= {SchemaStringBudget}");
    }

    [Fact]
    public void SchemaStringBudget_CountsOccurrencesAndEnumValuesWithoutDescriptionsOrRequiredCopies()
    {
        using var schema = JsonDocument.Parse("""
            {"properties":{"a":{"enum":["Day",12,null],"description":"Ignored"},
             "b":{"properties":{"a":{"enum":["Day"]}},"required":["a"]}},"required":["a","b"]}
            """);

        SchemaStringLength(schema.RootElement).Should().Be(15);
    }

    private static int SchemaStringLength(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Array) return schema.EnumerateArray().Sum(SchemaStringLength);
        if (schema.ValueKind != JsonValueKind.Object) return 0;
        var size = 0;
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name == "properties") size += property.Value.EnumerateObject().Sum(field => field.Name.Length);
            if (property.Name == "enum") size += property.Value.EnumerateArray().Sum(value =>
                value.ValueKind == JsonValueKind.String ? value.GetString()!.Length : value.GetRawText().Length);
            size += SchemaStringLength(property.Value);
        }
        return size;
    }
}
