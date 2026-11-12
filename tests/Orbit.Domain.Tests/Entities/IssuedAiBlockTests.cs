using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class IssuedAiBlockTests
{
    private static AiBlockDefinition Choice(int count = 2) => new("choice", "When?",
        Enumerable.Range(0, count).Select(i => new AiBlockOption(i.ToString(), "Option " + i)).ToArray(), []);

    [Fact]
    public void Create_SetsOwnerDefinitionAndLifetime()
    {
        var userId = Guid.NewGuid();
        var block = IssuedAiBlock.Create(userId, Choice());
        block.UserId.Should().Be(userId);
        block.ReadDefinition().Options.Should().HaveCount(2);
        (block.ExpiresAtUtc - block.CreatedAtUtc).Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData("call")]
    [InlineData("field")]
    [InlineData("clarification")]
    [InlineData("question")]
    [InlineData("pickList")]
    public void Create_RejectsIncompleteOrUnrelatedDraftBindings(string variant)
    {
        var binding = new AiDraftClarification(variant == "call" ? " " : "create-call",
            variant == "field" ? " " : "frequency_unit", variant == "clarification" ? Guid.Empty : Guid.NewGuid());
        var definition = Choice() with { DraftClarification = binding };
        if (variant == "question")
            definition = new("question", "", [], [], Questions: [new("unrelated", "yesNo", "Continue?")], DraftClarification: binding);
        if (variant == "pickList")
            definition = new("pickList", "Choose", [], [new("item", "Plant habit", JsonSerializer.SerializeToElement(new { title = "Plant habit" }))],
                MultiSelect: true, Action: "create_habits", DraftClarification: binding);

        Action create = () => IssuedAiBlock.Create(Guid.NewGuid(), definition);

        create.Should().Throw<ArgumentException>().WithParameterName("definition");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Create_RejectsChoiceCount(int count) =>
        ((Action)(() => IssuedAiBlock.Create(Guid.NewGuid(), Choice(count)))).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("label", 20, true)]
    [InlineData("label", 21, false)]
    [InlineData("description", 24, true)]
    [InlineData("description", 25, false)]
    public void Create_EnforcesChoiceTextLimits(string field, int count, bool valid)
    {
        var option = new AiBlockOption("a", field == "label" ? new string('l', count) : "Morning",
            field == "description" ? new string('d', count) : null);
        var definition = Choice() with { Options = [option, new("b", "Evening")] };
        Action create = () => IssuedAiBlock.Create(Guid.NewGuid(), definition);

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
        var item = new AiBlockItem("a", new string('l', 100), JsonSerializer.SerializeToElement(new { title = "Walk" }),
            count.HasValue ? new string('d', count.Value) : null);
        var definition = new AiBlockDefinition("pickList", "Choose", [], [item], true, Action: "create_habits");
        Action create = () => IssuedAiBlock.Create(Guid.NewGuid(), definition);

        if (valid)
            create.Should().NotThrow();
        else
            create.Should().Throw<ArgumentException>().WithParameterName("definition");
    }

    [Fact]
    public void MarkAnswered_RejectsReplayAndExpiry()
    {
        var block = IssuedAiBlock.Create(Guid.NewGuid(), Choice());
        block.MarkAnswered(block.CreatedAtUtc);
        ((Action)(() => block.MarkAnswered(block.CreatedAtUtc))).Should().Throw<InvalidOperationException>();
        var expired = IssuedAiBlock.Create(Guid.NewGuid(), Choice());
        ((Action)(() => expired.MarkAnswered(expired.ExpiresAtUtc))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Answer_MatchesSelectionAsSetAndExactFreeText()
    {
        var block = IssuedAiBlock.Create(Guid.NewGuid(), Choice() with { MultiSelect = true, AllowFreeText = true });
        block.MarkAnswered(block.CreatedAtUtc);
        typeof(IssuedAiBlock).GetProperty(nameof(IssuedAiBlock.AnswerJson))!
            .SetValue(block, IssuedAiBlock.SerializeAnswer(new(block.Id, ["1", "0"], "After dinner")));

        block.MatchesAnswer(new(block.Id, ["0", "1"], "After dinner")).Should().BeTrue();
        block.MatchesAnswer(new(block.Id, ["0"], "After dinner")).Should().BeFalse();
        block.MatchesAnswer(new(block.Id, ["0", "1"], "Before dinner")).Should().BeFalse();
        block.MatchesAnswer(new(Guid.NewGuid(), ["0", "1"], "After dinner")).Should().BeFalse();
    }

    [Fact]
    public void Create_KeepsOptionTargetIdsInTheStoredDefinitionAndDropsThemFromTheClientCopy()
    {
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var definition = new AiBlockDefinition("choice", "Keep which?",
            [new("first", "First", TargetIds: ids), new("rest", "The rest")], []);

        var block = IssuedAiBlock.Create(Guid.NewGuid(), definition);

        block.ReadDefinition().Options[0].TargetIds.Should().Equal(ids);
        block.ReadDefinition().Options[1].TargetIds.Should().BeNull();
        JsonSerializer.Serialize(block.ReadDefinition().Options[0].ForClient()).Should().NotContain("target_ids");
        JsonSerializer.Serialize(block.ReadDefinition().Options[1]).Should().NotContain("target_ids");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("not-a-guid")]
    [InlineData("empty-guid")]
    [InlineData("duplicate")]
    [InlineData("too-many")]
    public void Create_RejectsMalformedOptionTargetIds(string variant)
    {
        var id = Guid.NewGuid().ToString();
        IReadOnlyList<string> targets = variant switch
        {
            "empty" => [],
            "not-a-guid" => ["first"],
            "empty-guid" => [Guid.Empty.ToString()],
            "duplicate" => [id, id.ToUpperInvariant()],
            _ => Enumerable.Range(0, AiBlockOption.MaxTargetIds + 1).Select(_ => Guid.NewGuid().ToString()).ToArray()
        };
        var definition = new AiBlockDefinition("choice", "Keep which?",
            [new("first", "First", TargetIds: targets), new("rest", "The rest")], []);

        var create = () => IssuedAiBlock.Create(Guid.NewGuid(), definition);

        create.Should().Throw<ArgumentException>().WithMessage("target_ids*");
    }
}
