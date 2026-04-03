using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Application.Tests.Chat.Tools;

public class SuggestBreakdownToolTests
{
    private readonly SuggestBreakdownTool _tool = new();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void Name_IsSuggestBreakdown()
    {
        _tool.Name.Should().Be("suggest_breakdown");
    }

    [Fact]
    public async Task ExecuteAsync_WithTitle_ReturnsSuccessWithEntityName()
    {
        var args = JsonDocument.Parse("{\"title\": \"Exercise regularly\", \"suggested_sub_habits\": []}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Exercise regularly");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutTitle_ReturnsSuccessWithNullName()
    {
        var args = JsonDocument.Parse("{\"suggested_sub_habits\": []}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCreateAnything()
    {
        var args = JsonDocument.Parse("{\"title\": \"Test\", \"suggested_sub_habits\": [{\"title\": \"Sub1\"}]}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        // SuggestBreakdown only passes through suggestions, never creates entities
        result.Success.Should().BeTrue();
        result.EntityId.Should().BeNull();
    }

    [Fact]
    public void GetParameterSchema_ReturnsValidSchema()
    {
        var schema = _tool.GetParameterSchema();
        schema.Should().NotBeNull();
    }
}
