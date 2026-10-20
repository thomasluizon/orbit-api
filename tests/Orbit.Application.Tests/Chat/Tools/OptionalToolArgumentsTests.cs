using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class OptionalToolArgumentsTests
{
    [Theory]
    [InlineData("update_habit")]
    [InlineData("update_goal")]
    [InlineData("bulk_update_habits")]
    public void ClearableFields_ExplainTheExplicitClearContract(string operation)
    {
        using var context = new HeldWriteTestContext();
        var tool = context.Tool(operation);
        tool.Description.Should().Contain("clear_fields");
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        if (operation == "bulk_update_habits") schema = schema.GetProperty("properties").GetProperty("updates");
        foreach (var field in OptionalToolArguments.ClearableFields(operation))
            schema.GetProperty("properties").GetProperty(field).GetProperty("description").GetString()
                .Should().Contain("clear_fields");
    }

    [Fact]
    public void ModelNulls_LeaveRequiredAndUnknownArgumentsForRuntimeValidation()
    {
        var schema = new BulkUpdateHabitsTool(Substitute.For<IMediator>()).GetParameterSchema();
        var args = JsonSerializer.Deserialize<JsonElement>("""
            {"filter":{"all":true,"tag":null,"unknown":null},"updates":{"title":null,"unknown":null},"unknown":null}
            """);
        var result = OptionalToolArguments.OmitOptionalNulls(args, schema);
        result.GetProperty("unknown").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("updates").GetProperty("unknown").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("filter").GetProperty("unknown").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("filter").TryGetProperty("tag", out _).Should().BeFalse();
        result.GetProperty("updates").TryGetProperty("title", out _).Should().BeFalse();
        var required = OptionalToolArguments.OmitOptionalNulls(
            JsonSerializer.Deserialize<JsonElement>("""{"filter":null,"updates":null}"""), schema);
        required.GetProperty("filter").ValueKind.Should().Be(JsonValueKind.Null);
        required.GetProperty("updates").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("""{"clear_fields":["title"]}""")]
    [InlineData("""{"clear_fields":"description"}""")]
    [InlineData("""{"clear_fields":[null]}""")]
    [InlineData("""{"clear_fields":["description"],"description":"Keep"}""")]
    public void ClearFields_RejectUnsupportedOrConflictingWrites(string json)
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>(json);
        var result = OptionalToolArguments.ResolveClears(arguments,
            OptionalToolArguments.HabitClearFields);
        result.Error.Should().NotBeNull();
        var external = OptionalToolArguments.NormalizeExternalClears("update_habit", arguments);
        result = OptionalToolArguments.ResolveClears(external,
            OptionalToolArguments.HabitClearFields);
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public void ExternalNullClears_MergeWithClearFieldsWithoutConvertingOtherNulls()
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>("""
            {"description":null,"emoji":null,"clear_fields":["description"],"title":null,"frequency_quantity":null,"unknown":null}
            """);
        var external = OptionalToolArguments.NormalizeExternalClears("update_habit", arguments);

        external.GetProperty("clear_fields").EnumerateArray().Select(field => field.GetString())
            .Should().Equal("description", "emoji");
        external.GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        external.GetProperty("frequency_quantity").ValueKind.Should().Be(JsonValueKind.Null);
        external.GetProperty("unknown").ValueKind.Should().Be(JsonValueKind.Null);
        var resolved = OptionalToolArguments.ResolveClears(external, OptionalToolArguments.HabitClearFields);
        resolved.Error.Should().BeNull();
        resolved.Arguments.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);
        resolved.Arguments.GetProperty("emoji").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void ExternalBulkNullClears_ReachTheSharedChangesParser()
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>("""
            {"updates":{"description":null,"emoji":null,"frequency_unit":null,"frequency_quantity":null,
                        "interval_weeks":null,"end_date":null,"due_time":null}}
            """);
        var external = OptionalToolArguments.NormalizeExternalClears("bulk_update_habits", arguments);

        var (changes, error) = BulkHabitToolArguments.ParseChanges(external);

        error.Should().BeNull();
        changes.Should().NotBeNull();
        changes!.HasDescription.Should().BeTrue();
        changes.Description.Should().BeNull();
        changes.HasEmoji.Should().BeTrue();
        changes.Emoji.Should().BeNull();
        changes.HasFrequencyUnit.Should().BeTrue();
        changes.FrequencyUnit.Should().BeNull();
        changes.HasFrequencyQuantity.Should().BeTrue();
        changes.FrequencyQuantity.Should().BeNull();
        changes.HasIntervalWeeks.Should().BeTrue();
        changes.IntervalWeeks.Should().BeNull();
        changes.HasEndDate.Should().BeTrue();
        changes.EndDate.Should().BeNull();
        changes.HasDueTime.Should().BeTrue();
        changes.DueTime.Should().BeNull();
        changes.HasTitle.Should().BeFalse();
    }

    [Fact]
    public void ExternalBulkRevisions_KeepExplicitClears()
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>("""
            {"revised_items":[{"emoji":null},{"description":null,"clear_fields":["due_time"]}]}
            """);
        var external = OptionalToolArguments.NormalizeExternalClears("bulk_update_habits", arguments);
        var items = external.GetProperty("revised_items").EnumerateArray().ToArray();
        var first = OptionalToolArguments.ResolveClears(items[0], OptionalToolArguments.BulkHabitClearFields);
        var second = OptionalToolArguments.ResolveClears(items[1], OptionalToolArguments.BulkHabitClearFields);

        first.Error.Should().BeNull();
        first.Arguments.GetProperty("emoji").ValueKind.Should().Be(JsonValueKind.Null);
        second.Error.Should().BeNull();
        second.Arguments.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);
        second.Arguments.GetProperty("due_time").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
