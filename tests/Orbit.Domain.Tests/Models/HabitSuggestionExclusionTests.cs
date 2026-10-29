using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public sealed class HabitSuggestionExclusionTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void ExclusionCount_IsBounded(int count, bool valid) =>
        HabitSuggestionRequest.HasValidExclusions(Enumerable.Repeat("Read", count).ToArray()).Should().Be(valid);

    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("!!!", false)]
    [InlineData("📖", false)]
    [InlineData("Meditação", true)]
    [InlineData("阅读", true)]
    public void ExclusionTitle_RequiresActivityText(string title, bool valid) =>
        HabitSuggestionRequest.HasValidExclusions([title]).Should().Be(valid);

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void ExclusionTitleLength_IsBounded(int length, bool valid) =>
        HabitSuggestionRequest.HasValidExclusions([new string('x', length)]).Should().Be(valid);

    [Fact]
    public void OmittedExclusions_PreserveOrdinarySuggestions() =>
        HabitSuggestionRequest.HasValidExclusions(null).Should().BeTrue();
}
