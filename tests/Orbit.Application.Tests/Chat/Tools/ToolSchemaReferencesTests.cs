using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class ToolSchemaReferencesTests
{
    [Fact]
    public void MissingSchema_KeepsPreviewFieldsUndeclared()
    {
        AgentArgumentSchema.Declares(default, "title").Should().BeFalse();
        AgentArgumentSchema.Accepts(default, "title", JsonSerializer.SerializeToElement("Read")).Should().BeFalse();
    }

    [Fact]
    public void RecursiveSchema_AcceptsNestedValuesAndRejectsInvalidNestedFields()
    {
        var tool = new BulkCreateHabitsTool(Substitute.For<IMediator>());
        var schema = JsonSerializer.SerializeToElement(ToolSchemaReferences.Compact(tool.GetParameterSchema()));
        var valid = JsonSerializer.Deserialize<JsonElement>("""
            [{"title":"Routine","sub_habits":[{"title":"Read","frequency_unit":"Day","days":["Monday"]}]}]
            """);
        var invalid = JsonSerializer.Deserialize<JsonElement>("""
            [{"title":"Routine","sub_habits":[{"title":"Read","frequency_unit":"invalid"}]}]
            """);
        AgentArgumentSchema.Accepts(schema, "habits", valid).Should().BeTrue();
        AgentArgumentSchema.Accepts(schema, "habits", invalid).Should().BeFalse();
        AgentArgumentSchema.Accepts(schema, "habits", JsonSerializer.Deserialize<JsonElement>("""
            [{"title":"Routine","sub_habits":[{"description":"Missing title"}]}]
            """)).Should().BeFalse();
    }

    [Fact]
    public void RecursiveSchema_OmitsNestedOptionalNullsAndPreservesRequiredNulls()
    {
        var tool = new BulkCreateHabitsTool(Substitute.For<IMediator>());
        var arguments = JsonSerializer.Deserialize<JsonElement>("""
            {"habits":[{"title":"Routine","sub_habits":[{"title":null,"frequency_unit":null,"repeat_reminder":null}]}]}
            """);
        var result = OptionalToolArguments.OmitOptionalNulls(arguments, ToolSchemaReferences.Compact(tool.GetParameterSchema()));
        var child = result.GetProperty("habits")[0].GetProperty("sub_habits")[0];
        child.GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        child.TryGetProperty("frequency_unit", out _).Should().BeFalse();
        child.TryGetProperty("repeat_reminder", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task StructuredCall_RespectsTheExistingServerDepth(int levels, bool valid)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tool = new BulkCreateHabitsTool(mediator);
        var item = new System.Text.Json.Nodes.JsonObject { ["title"] = "Activity" };
        for (var level = 1; level < levels; level++)
            item = new() { ["title"] = "Routine", ["sub_habits"] = new System.Text.Json.Nodes.JsonArray(item) };
        var call = new Orbit.Domain.Models.AiToolCall(tool.Name, "depth", JsonSerializer.SerializeToElement(new { habits = new[] { item } }));

        var result = await tool.CheckArgumentsAsync(call.Args, Guid.NewGuid(), default);

        result.IsSuccess.Should().Be(valid);
        if (!valid)
        {
            result.Error.Should().Be("Invalid tool arguments.");
            await mediator.DidNotReceive().Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>());
        }
    }
}
