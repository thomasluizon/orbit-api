using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Tools;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class OptionalParameterCasesTests
{
    [Theory]
    [MemberData(nameof(OptionalParameterCases.Schemas), MemberType = typeof(OptionalParameterCases))]
    public void Fields_MatchLiveSchema(Type toolType, string path)
    {
        var tool = (IAiTool)RuntimeHelpers.GetUninitializedObject(toolType);
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            schema = segment == "items" ? schema.GetProperty("items") : schema.GetProperty("properties").GetProperty(segment);
        var required = schema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()).ToHashSet() : [];
        var optional = schema.GetProperty("properties").EnumerateObject()
            .Where(property => !required.Contains(property.Name)).Select(property => property.Name);

        OptionalParameterCases.Fields(toolType, path).Select(row => (string)row[0]).Should().Equal(optional);
    }

    [Fact]
    public void Fields_RejectsUnregisteredToolWithoutEvaluatingItsSchema()
    {
        DiscoveryProbeTool.SchemaCalls = 0;

        var exception = Record.Exception(() => OptionalParameterCases.Fields(typeof(DiscoveryProbeTool), "").ToArray());

        DiscoveryProbeTool.SchemaCalls.Should().Be(0, "theory discovery must not execute production schemas outside a test's coverage window");
        exception.Should().BeOfType<KeyNotFoundException>();
    }

    private sealed class DiscoveryProbeTool : IAiTool
    {
        public static int SchemaCalls { get; set; }
        public string Name => "discovery_probe";
        public string Description => "Discovery probe";

        public object GetParameterSchema()
        {
            SchemaCalls++;
            return new { type = "object", properties = new { optional = new { type = "string" } } };
        }

        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
