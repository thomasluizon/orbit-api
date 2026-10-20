using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class OptionalToolArgumentsTests
{
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
        var result = OptionalToolArguments.ResolveClears(JsonSerializer.Deserialize<JsonElement>(json),
            OptionalToolArguments.HabitClearFields);
        result.Error.Should().NotBeNull();
    }
}
