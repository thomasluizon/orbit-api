using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using NSubstitute;
using MediatR;
using Orbit.Domain.Interfaces;
using FluentAssertions;

namespace Orbit.Application.Tests.Chat.Tools;

public partial class CreateHabitToolTests
{
    [Fact]
    public void GetParameterSchema_MatchesReviewedContract()
    {
        using var stream = typeof(CreateHabitToolTests).Assembly.GetManifestResourceStream("CreateHabitTool.schema.json")
            ?? throw new InvalidOperationException("The create_habit schema contract must be embedded in the test assembly.");
        using var expected = JsonDocument.Parse(stream);

        var original = JsonNode.Parse(expected.RootElement.GetRawText())!;
        WithoutDescriptions(JsonSerializer.SerializeToNode(_tool.GetParameterSchema())!).ToJsonString()
            .Should().Be(WithoutDescriptions(original.DeepClone()).ToJsonString());
        var bulk = new BulkCreateHabitsTool(Substitute.For<IMediator>());
        var bulkSchema = JsonSerializer.SerializeToNode(bulk.GetParameterSchema())!;
        WithoutDescriptions(bulkSchema["properties"]!["habits"]!["items"]!).ToJsonString()
            .Should().Be(WithoutDescriptions(original.DeepClone()).ToJsonString());
        var ask = new AskTool(bulk, new BulkLogHabitsTool(Substitute.For<IMediator>(),
            Substitute.For<IGenericRepository<Orbit.Domain.Entities.Habit>>(), Substitute.For<IUserDateService>(),
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), Substitute.For<IUnitOfWork>())),
            new BulkSkipHabitsTool(Substitute.For<IMediator>(),
            Substitute.For<IGenericRepository<Orbit.Domain.Entities.Habit>>(), Substitute.For<IUserDateService>(),
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), Substitute.For<IUnitOfWork>())),
            Substitute.For<IUserDateService>());
        var askSchema = JsonSerializer.SerializeToNode(ask.GetParameterSchema())!;
        WithoutDescriptions(askSchema["properties"]!["input"]!["anyOf"]![1]!["properties"]!["items"]!["items"]!["properties"]!["arguments"]!["anyOf"]![0]!).ToJsonString()
            .Should().Be(WithoutDescriptions(original.DeepClone()).ToJsonString());
        var sub = JsonSerializer.SerializeToNode(new CreateSubHabitTool(Substitute.For<IMediator>()).GetParameterSchema())!;
        var expectedSub = original.DeepClone();
        var leaf = expectedSub;
        for (var level = 1; level < AppConstants.MaxHabitDepth - 1; level++)
            leaf = leaf["properties"]!["sub_habits"]!["items"]!;
        leaf["properties"]!.AsObject().Remove("sub_habits");
        leaf["properties"]!.AsObject().Remove("parent_habit_id");
        expectedSub["required"] = new JsonArray("parent_habit_id", "title");
        WithoutDescriptions(sub).ToJsonString().Should().Be(WithoutDescriptions(expectedSub).ToJsonString());
    }
    private static JsonNode WithoutDescriptions(JsonNode node)
    {
        var schema = node.AsObject();
        schema.Remove("description");
        if (schema["properties"] is JsonObject properties)
            foreach (var field in properties)
                WithoutDescriptions(field.Value!);
        if (schema["items"] is JsonObject items)
            WithoutDescriptions(items);
        if (schema["anyOf"] is JsonArray alternatives)
            foreach (var alternative in alternatives)
                WithoutDescriptions(alternative!);
        return node;
    }
}
