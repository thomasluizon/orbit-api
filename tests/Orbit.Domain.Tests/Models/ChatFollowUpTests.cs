using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public sealed class ChatFollowUpTests
{
    [Theory]
    [InlineData("", "Next?", null)]
    [InlineData("Next", "", null)]
    [InlineData("Next", "Next?", "unknown")]
    [InlineData("Next\nline", "Next?", null)]
    public void Constructor_RejectsInvalidTextAndTarget(string label, string question, string? target)
    {
        Action create = () => _ = new ChatFollowUp(label, question, target: target);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_PreservesContext()
    {
        var query = Guid.NewGuid();
        var item = new ChatFollowUp("Habits", "Show my habits?", "query_habits", "habits", query);
        item.QueryId.Should().Be(query);
        item.Question.Should().Be("Show my habits?");
    }
}
