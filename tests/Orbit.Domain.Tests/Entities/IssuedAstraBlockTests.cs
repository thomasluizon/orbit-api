using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class IssuedAstraBlockTests
{
    private static AstraBlockDefinition Choice(int count = 2) => new("choice", "When?",
        Enumerable.Range(0, count).Select(i => new AstraBlockOption(i.ToString(), "Option " + i)).ToArray(), []);

    [Fact]
    public void Create_SetsOwnerDefinitionAndLifetime()
    {
        var userId = Guid.NewGuid();
        var block = IssuedAstraBlock.Create(userId, Choice());
        block.UserId.Should().Be(userId);
        block.ReadDefinition().Options.Should().HaveCount(2);
        (block.ExpiresAtUtc - block.CreatedAtUtc).Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Create_RejectsChoiceCount(int count) =>
        ((Action)(() => IssuedAstraBlock.Create(Guid.NewGuid(), Choice(count)))).Should().Throw<ArgumentException>();

    [Fact]
    public void MarkAnswered_RejectsReplayAndExpiry()
    {
        var block = IssuedAstraBlock.Create(Guid.NewGuid(), Choice());
        block.MarkAnswered(block.CreatedAtUtc);
        ((Action)(() => block.MarkAnswered(block.CreatedAtUtc))).Should().Throw<InvalidOperationException>();
        var expired = IssuedAstraBlock.Create(Guid.NewGuid(), Choice());
        ((Action)(() => expired.MarkAnswered(expired.ExpiresAtUtc))).Should().Throw<InvalidOperationException>();
    }
}
