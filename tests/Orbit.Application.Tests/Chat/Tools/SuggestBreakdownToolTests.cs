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

        result.Success.Should().BeTrue();
        result.EntityId.Should().BeNull();
    }

    [Fact]
    public void GetParameterSchema_ReturnsValidSchema()
    {
        var schema = _tool.GetParameterSchema();
        schema.Should().NotBeNull();
    }

    [Fact]
    public void SmallerGuidance_RequiresLessTimeAndEffortThanStructuredPreviousStep()
    {
        const string guidance = "For a smaller refinement, use structured previousStartingStep as the baseline, keep its goal and return a proper part of its step or its first physical action, taking strictly less time and effort. A paraphrase or a different action of equal or greater effort is not smaller.";
        _tool.Description.Should().Contain(guidance);
        var schema = JsonSerializer.SerializeToElement(_tool.GetParameterSchema());
        schema.GetProperty("properties").GetProperty("one_small_step").GetProperty("description").GetString()
            .Should().Be("Return exactly one starting step as a oneSmallStep block. For a refinement, use structured previousStartingStep and return a proper part of its step or its first physical action, taking strictly less time and effort. Default false.");
    }
    [Fact]
    public async Task ExecuteAsync_OneSmallStep_RejectsSeveralSteps()
    {
        var args = JsonDocument.Parse("""{"title":"Study","one_small_step":true,"language":"en","suggested_sub_habits":[{"title":"Open the book"},{"title":"Read a page"}]}""").RootElement;

        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
    }
}
