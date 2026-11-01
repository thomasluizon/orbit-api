#pragma warning disable OPENAI001

using System.IO.Compression;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Models;
using System.ClientModel.Primitives;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiInputBudgetTests
{
    [Fact]
    public async Task RecursiveToolCall_OverServerDepthReturnsValidationFeedbackToModel()
    {
        var mediator = Substitute.For<IMediator>();
        var tool = new BulkCreateHabitsTool(mediator);
        var item = new JsonObject { ["title"] = "Activity" };
        for (var level = 1; level < 6; level++)
            item = new() { ["title"] = "Routine", ["sub_habits"] = new JsonArray(item) };
        var arguments = JsonSerializer.SerializeToElement(new { habits = new[] { item } });
        var response = ResponsesTestData.Response();
        response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("too-deep", tool.Name, BinaryData.FromString(arguments.GetRawText())));
        var transport = new SequenceHandler(ModelReaderWriter.Write(response).ToString(), ResponsesTestData.Text("Please revise the hierarchy."));
        var service = BuildService(transport);
        var first = await service.SendWithToolsAsync(new("Organize my activities.", "Use the declared tools.",
            [new { name = tool.Name, description = tool.Description, parameters = tool.GetParameterSchema() }]));
        first.IsSuccess.Should().BeTrue();
        var call = first.Value.ToolCalls.Should().ContainSingle().Subject;
        var validation = await tool.CheckArgumentsAsync(call.Args, Guid.NewGuid(), default);
        validation.Error.Should().Be("Invalid tool arguments.");

        var continued = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new(call.Name, call.Id, validation.IsSuccess, null, null, validation.Error)]);

        continued.IsSuccess.Should().BeTrue();
        transport.Bodies.Should().HaveCount(2);
        using var request = JsonDocument.Parse(transport.Bodies[1]);
        var result = request.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "function_call_output");
        using var feedback = JsonDocument.Parse(result.GetProperty("output").GetString()!);
        feedback.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        feedback.RootElement.GetProperty("error").GetString().Should().Be("Invalid tool arguments.");
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Schemas_ReportCatalogSizes()
    {
        using var stream = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/ToolSchemas/inlined.json.gz")), CompressionMode.Decompress);
        var baseline = (await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(stream))!;
        var table = new System.Text.StringBuilder("| Client | Tool | Before JSON characters | After JSON characters |\n| --- | --- | ---: | ---: |\n");
        foreach (var client in new[] { "legacy", "composition", "offers" })
        {
            var captures = await CaptureCacheTurnsAsync(client);
            using var request = JsonDocument.Parse(captures[0].Body);
            foreach (var tool in request.RootElement.GetProperty("tools").EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString()!;
                var before = JsonNode.Parse(tool.GetRawText())!.AsObject();
                before["parameters"] = JsonNode.Parse(baseline[client + "/" + name].GetRawText());
                var beforeCount = Canonical(JsonSerializer.SerializeToElement(before)).Length;
                var afterCount = Canonical(tool).Length;
                if (Math.Max(beforeCount, afterCount) > 3000)
                    table.AppendLine($"| {client} | {name} | {beforeCount} | {afterCount} |");
            }
        }
        output.WriteLine(table.ToString());
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "orbit-1916-catalog-sizes.md"), table.ToString());
    }

    [Fact]
    public void HabitSchema_UsesRecursiveDefinition()
    {
        var schema = RegisteredTools().Single(tool => tool.Name == "create_habit").GetParameterSchema();
        var compact = ToolSchemaReferences.Compact(schema);
        compact["$defs"]!.AsObject().ContainsKey("habit").Should().BeTrue(compact.ToJsonString());
    }

    [Fact]
    public async Task Schemas_ExpandToTheReviewedInlinedContractWithinServerDepth()
    {
        using var stream = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/ToolSchemas/inlined.json.gz")), CompressionMode.Decompress);
        var baseline = (await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(stream))!;
        var schemas = await CurrentToolSchemas();
        schemas.Keys.Should().BeEquivalentTo(baseline.Keys);
        foreach (var (name, original) in baseline)
        {
            var current = name.StartsWith("source/", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(ToolSchemaReferences.Compact(schemas[name])) : schemas[name];
            var maximum = name.EndsWith("/create_sub_habit", StringComparison.Ordinal) ? 4 : 5;
            var expanded = ToolSchemaExpansion.Expand(current, maximum);
            JsonElement.DeepEquals(expanded, original).Should().BeTrue(name + " constraints and descriptions must survive reference expansion");
        }
    }


}
