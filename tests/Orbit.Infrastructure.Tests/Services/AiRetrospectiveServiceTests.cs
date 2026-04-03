using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure helper methods shared/used by AiRetrospectiveService.
/// The AI client orchestration is tested at the integration level.
/// StripMarkdownFences is the shared utility from AiSummaryService.
/// </summary>
public class AiRetrospectiveServiceTests
{
    // ── StripMarkdownFences edge cases specific to retrospective output ──

    [Fact]
    public void StripMarkdownFences_BoldHeadings_PreservesBoldMarkdown()
    {
        var text = "**Highlights**\nGreat job!\n\n**Missed Opportunities**\nCould improve.";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("**Highlights**");
        result.Should().Contain("**Missed Opportunities**");
    }

    [Fact]
    public void StripMarkdownFences_FencedRetrospective_StripsCorrectly()
    {
        var text = "```\n**Highlights**\nYou nailed exercise.\n**Trends**\nGoing strong.\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("**Highlights**");
        result.Should().NotStartWith("```");
        result.Should().NotEndWith("```");
    }

    [Fact]
    public void StripMarkdownFences_EmptyInput_ReturnsEmpty()
    {
        AiSummaryService.StripMarkdownFences("").Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_OnlyFences_ReturnsEmpty()
    {
        var text = "```\n```";
        AiSummaryService.StripMarkdownFences(text).Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_NestedFences_HandlesGracefully()
    {
        // Should strip the outer fences
        var text = "```\nSome code:\n```inner```\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Some code:");
    }
}
