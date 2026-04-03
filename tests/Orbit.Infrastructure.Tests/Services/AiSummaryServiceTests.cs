using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure logic in AiSummaryService: StripMarkdownFences and prompt structure.
/// The AI client call itself is an integration concern tested elsewhere.
/// </summary>
public class AiSummaryServiceTests
{
    // ── StripMarkdownFences ──────────────────────────────────────────

    [Fact]
    public void StripMarkdownFences_PlainText_ReturnsUnchanged()
    {
        var text = "Just a plain summary.";
        AiSummaryService.StripMarkdownFences(text).Should().Be(text);
    }

    [Fact]
    public void StripMarkdownFences_WithFences_RemovesThem()
    {
        var text = "```\nHello world\n```";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Hello world");
    }

    [Fact]
    public void StripMarkdownFences_WithLanguageFences_RemovesThem()
    {
        var text = "```markdown\nContent here\n```";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Content here");
    }

    [Fact]
    public void StripMarkdownFences_FencesWithoutClosing_RemovesOpening()
    {
        var text = "```\nNo closing fence";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Be("No closing fence");
    }

    [Fact]
    public void StripMarkdownFences_WhitespaceOnly_ReturnsTrimmed()
    {
        var text = "   \n   ";
        AiSummaryService.StripMarkdownFences(text).Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_MultipleLines_PreservesContent()
    {
        var text = "```\nLine 1\nLine 2\nLine 3\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Line 1");
        result.Should().Contain("Line 2");
        result.Should().Contain("Line 3");
    }

    [Fact]
    public void StripMarkdownFences_LeadingTrailingWhitespace_Trims()
    {
        var text = "  \n  Some text  \n  ";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Some text");
    }
}
