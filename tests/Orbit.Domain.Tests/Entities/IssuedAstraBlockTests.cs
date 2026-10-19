using System.Text.Json;
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

    [Theory]
    [InlineData("label", 20, true)]
    [InlineData("label", 21, false)]
    [InlineData("description", 24, true)]
    [InlineData("description", 25, false)]
    public void Create_EnforcesChoiceTextLimits(string field, int count, bool valid)
    {
        var option = new AstraBlockOption("a", field == "label" ? new string('l', count) : "Morning",
            field == "description" ? new string('d', count) : null);
        var definition = Choice() with { Options = [option, new("b", "Evening")] };
        Action create = () => IssuedAstraBlock.Create(Guid.NewGuid(), definition);

        if (valid)
            create.Should().NotThrow();
        else
            create.Should().Throw<ArgumentException>().WithParameterName("definition");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void Create_EnforcesPickListDetailLimitWithoutCappingHabitLabel(int? count, bool valid)
    {
        var item = new AstraBlockItem("a", new string('l', 100), JsonSerializer.SerializeToElement(new { title = "Walk" }),
            count.HasValue ? new string('d', count.Value) : null);
        var definition = new AstraBlockDefinition("pickList", "Choose", [], [item], true, Action: "create_habits");
        Action create = () => IssuedAstraBlock.Create(Guid.NewGuid(), definition);

        if (valid)
            create.Should().NotThrow();
        else
            create.Should().Throw<ArgumentException>().WithParameterName("definition");
    }

    [Fact]
    public void MarkAnswered_RejectsReplayAndExpiry()
    {
        var block = IssuedAstraBlock.Create(Guid.NewGuid(), Choice());
        block.MarkAnswered(block.CreatedAtUtc);
        ((Action)(() => block.MarkAnswered(block.CreatedAtUtc))).Should().Throw<InvalidOperationException>();
        var expired = IssuedAstraBlock.Create(Guid.NewGuid(), Choice());
        ((Action)(() => expired.MarkAnswered(expired.ExpiresAtUtc))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Answer_MatchesSelectionAsSetAndExactFreeText()
    {
        var block = IssuedAstraBlock.Create(Guid.NewGuid(), Choice() with { MultiSelect = true, AllowFreeText = true });
        block.MarkAnswered(block.CreatedAtUtc);
        block.RecordAnswer(new(block.Id, ["1", "0"], "After dinner"), new(2026, 4, 3));

        block.MatchesAnswer(new(block.Id, ["0", "1"], "After dinner")).Should().BeTrue();
        block.MatchesAnswer(new(block.Id, ["0"], "After dinner")).Should().BeFalse();
        block.MatchesAnswer(new(block.Id, ["0", "1"], "Before dinner")).Should().BeFalse();
        block.MatchesAnswer(new(Guid.NewGuid(), ["0", "1"], "After dinner")).Should().BeFalse();
    }

    [Fact]
    public void ReleaseAnswer_ClearsReservationAndAllowsAnotherAnswer()
    {
        var block = IssuedAstraBlock.Create(Guid.NewGuid(), Choice());
        block.MarkAnswered(block.CreatedAtUtc);
        block.RecordAnswer(new(block.Id, ["0"]), new(2026, 4, 3));
        block.ReleaseAnswer();
        block.AnsweredAtUtc.Should().BeNull();
        block.AnswerJson.Should().BeNull();
        block.ReservedAllowanceDate.Should().BeNull();
        block.MarkAnswered(block.CreatedAtUtc.AddSeconds(1));
    }

    [Fact]
    public void CompleteAnswer_RequiresReservationAndPreventsReleaseOrReplacement()
    {
        var block = IssuedAstraBlock.Create(Guid.NewGuid(), Choice());
        ((Action)(() => block.CompleteAnswer("{}"))).Should().Throw<InvalidOperationException>();
        ((Action)(() => block.RecordAnswer(new(block.Id, ["0"]), null))).Should().Throw<InvalidOperationException>();
        block.MarkAnswered(block.CreatedAtUtc);
        ((Action)(() => block.RecordAnswer(new(Guid.NewGuid(), ["0"]), null))).Should().Throw<InvalidOperationException>();
        block.RecordAnswer(new(block.Id, ["0"]), null);
        ((Action)(() => block.RecordAnswer(new(block.Id, ["1"]), null))).Should().Throw<InvalidOperationException>();
        ((Action)(() => block.CompleteAnswer(" "))).Should().Throw<ArgumentException>();
        block.CompleteAnswer("{}");
        ((Action)(() => block.ReleaseAnswer())).Should().Throw<InvalidOperationException>();
        ((Action)(() => block.CompleteAnswer("{}"))).Should().Throw<InvalidOperationException>();
    }
}
