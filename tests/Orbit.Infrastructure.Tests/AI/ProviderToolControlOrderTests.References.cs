#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Orbit.Application.Chat.References;

namespace Orbit.Infrastructure.Tests.AI;

/// <summary>
/// The closed list of ID arguments at the chat boundary (thomasluizon/orbit-tickets#2154, acceptance check 5). The
/// provider catalogs hold every model-facing schema, and the write tools in them are the operation schemas that
/// <c>edits_json</c> and a pickList item use.
/// </summary>
public sealed partial class ProviderToolControlOrderTests
{
    [Fact]
    public async Task EveryIdArgumentOfEveryChatCatalogHasARecordTypeOrTheLabelNoReference()
    {
        var unlisted = new SortedSet<string>(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        var idWords = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var client in new[] { "legacy", "composition", "offers" })
            foreach (var phase in new[] { "initial", "post-read", "draft-revision", "post-read-draft" })
            {
                var (_, tools) = await BuildProviderCatalogAsync(client, phase);
                foreach (var (name, tool) in tools)
                {
                    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(tool).ToString());
                    CollectIdArguments(wire.RootElement.GetProperty("parameters"), name, unlisted, named, idWords);
                }
            }

        output.WriteLine("ID arguments: " + string.Join(", ", named));
        unlisted.Should().BeEmpty("each ID argument needs a record type or the label \"no reference\"");
        named.Should().Contain(ReferenceArguments.Typed.Keys.Where(key => key != "preview_item_id"),
            "each typed argument of the closed list is an argument of a chat schema");
        idWords.Should().BeEmpty("the description of a typed ID argument names a reference, not an ID");
    }

    private static void CollectIdArguments(JsonElement schema, string tool, ISet<string> unlisted, ISet<string> named, ISet<string> idWords)
    {
        if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray()) CollectIdArguments(item, tool, unlisted, named, idWords);
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var property in properties.EnumerateObject().Where(property => ReferenceArguments.IsIdArgument(property.Name)))
            {
                named.Add(property.Name);
                if (!ReferenceArguments.Typed.ContainsKey(property.Name) && !ReferenceArguments.Unreferenced.Contains(property.Name))
                    unlisted.Add($"{tool}.{property.Name}");
                if (ReferenceArguments.Typed.ContainsKey(property.Name)
                    && property.Value.TryGetProperty("description", out var description)
                    && Regex.IsMatch(description.GetString() ?? string.Empty, @"\b(?:GUID|ID)s?\b"))
                    idWords.Add($"{tool}.{property.Name}");
            }
        foreach (var property in schema.EnumerateObject())
            CollectIdArguments(property.Value, tool, unlisted, named, idWords);
    }
}
