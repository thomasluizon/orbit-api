using System.IO.Compression;
using System.Text.Json;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiInputBudgetTests
{
    [Fact]
    public async Task SchemaSnapshots_ExportCurrentContracts()
    {
        var schemas = await CurrentToolSchemas();
        var path = Path.Combine(Path.GetTempPath(), "orbit-1916-tool-schemas.json.gz");
        await using var stream = new GZipStream(File.Create(path), CompressionLevel.SmallestSize);
        await JsonSerializer.SerializeAsync(stream, schemas);
        output.WriteLine(path);
    }

    private static async Task<SortedDictionary<string, JsonElement>> CurrentToolSchemas()
    {
        var schemas = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var tool in RegisteredTools())
            schemas.Add("source/" + tool.Name, JsonSerializer.SerializeToElement(tool.GetParameterSchema()));
        foreach (var client in new[] { "legacy", "composition", "offers" })
        {
            var captures = await CaptureCacheTurnsAsync(client);
            using var request = JsonDocument.Parse(captures[0].Body);
            foreach (var tool in request.RootElement.GetProperty("tools").EnumerateArray())
                schemas.Add(client + "/" + tool.GetProperty("name").GetString(), tool.GetProperty("parameters").Clone());
        }
        return schemas;
    }
}
