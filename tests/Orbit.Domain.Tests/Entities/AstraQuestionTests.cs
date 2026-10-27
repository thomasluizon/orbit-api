using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class AstraQuestionTests
{
    private static AstraQuestion Choice(string type = "singleChoice", bool custom = true) => new("q", type, "When?",
        [new("a", "Morning"), new("b", "Evening")], custom);
    private static AstraBlockDefinition Definition(AstraQuestion question) => new("question", "", [], [], Questions: [question]);

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(-1, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(2, 1, false)]
    public void Progress_AcceptsZeroThroughTotal(int current, int total, bool valid)
    {
        var create = () => IssuedAstraBlock.Create(Guid.NewGuid(), Definition(Choice()) with { Progress = new(current, total) });
        if (valid) create.Should().NotThrow();
        else create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("singleChoice")]
    [InlineData("multipleChoice")]
    [InlineData("scale")]
    [InlineData("slider")]
    [InlineData("yesNo")]
    public void Factory_PersistsEachQuestionTypeWithoutATitle(string type)
    {
        var question = type switch
        {
            "scale" => new("q", type, "Energy?", LowLabel: "Low", HighLabel: "High"),
            "slider" => new("q", type, "Duration?", Min: 0, Max: 1, Step: 0.1m, Unit: "hours"),
            "yesNo" => new("q", type, "Ready?", AllowFreeText: true),
            _ => Choice(type)
        };
        var block = IssuedAstraBlock.Create(Guid.NewGuid(), Definition(question));
        block.ReadDefinition().Should().BeEquivalentTo(Definition(question));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("many")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("prompt")]
    [InlineData("type")]
    [InlineData("options")]
    [InlineData("duplicateOptions")]
    [InlineData("nullOption")]
    [InlineData("recommendations")]
    [InlineData("multiRecommendation")]
    [InlineData("scaleLabels")]
    [InlineData("scaleCustom")]
    [InlineData("sliderBounds")]
    [InlineData("sliderStep")]
    [InlineData("sliderAlignment")]
    [InlineData("sliderUnit")]
    [InlineData("extraField")]
    [InlineData("yesOptions")]
    [InlineData("progress")]
    [InlineData("intro")]
    [InlineData("action")]
    public void Factory_RejectsInvalidDefinitions(string defect)
    {
        var definition = Definition(Choice());
        definition = defect switch
        {
            "empty" => definition with { Questions = [] },
            "many" => definition with { Questions = Enumerable.Range(0, 11).Select(i => Choice() with { Id = i.ToString() }).ToArray() },
            "duplicate" => definition with { Questions = [Choice(), Choice()] },
            "null" => definition with { Questions = [null!] },
            "prompt" => Definition(Choice() with { Prompt = " " }),
            "type" => Definition(Choice() with { Type = "unknown" }),
            "options" => Definition(Choice() with { Options = [] }),
            "duplicateOptions" => Definition(Choice() with { Options = [new("a", "First"), new("a", "Second")] }),
            "nullOption" => Definition(Choice() with { Options = [null!, new("b", "Second")] }),
            "recommendations" => Definition(Choice() with { Options = [new("a", "First", true), new("b", "Second", true)] }),
            "multiRecommendation" => Definition(Choice("multipleChoice") with { Options = [new("a", "First", true), new("b", "Second")] }),
            "scaleLabels" => Definition(new("q", "scale", "Energy?")),
            "scaleCustom" => Definition(new("q", "scale", "Energy?", AllowFreeText: true, LowLabel: "Low", HighLabel: "High")),
            "sliderBounds" => Definition(new("q", "slider", "Duration?", Min: 1, Max: 0, Step: 1, Unit: "min")),
            "sliderStep" => Definition(new("q", "slider", "Duration?", Min: 0, Max: 10, Step: 0, Unit: "min")),
            "sliderAlignment" => Definition(new("q", "slider", "Duration?", Min: 0, Max: 10, Step: 3, Unit: "min")),
            "sliderUnit" => Definition(new("q", "slider", "Duration?", Min: 0, Max: 10, Step: 1)),
            "extraField" => Definition(Choice() with { Min = 0 }),
            "yesOptions" => Definition(Choice("yesNo")),
            "progress" => definition with { Progress = new(2, 1) },
            "intro" => definition with { Intro = new string('a', 501) },
            "action" => definition with { Action = "create_habits" },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var create = () => IssuedAstraBlock.Create(Guid.NewGuid(), definition);
        create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("singleChoice", "a", null, true)]
    [InlineData("singleChoice", null, "Other", true)]
    [InlineData("singleChoice", "a", "Other", false)]
    [InlineData("multipleChoice", null, "Other", true)]
    [InlineData("multipleChoice", "a", "Other", true)]
    [InlineData("singleChoice", "unknown", null, false)]
    [InlineData("multipleChoice", "unknown", null, false)]
    [InlineData("multipleChoice", null, null, false)]
    public void Choices_ValidateSelectionAndCustomAnswers(string type, string? id, string? text, bool valid)
    {
        var answer = new AstraBlockAnswer(Guid.NewGuid(), [], Answers: [new("q", id is null ? [] : [id], FreeText: text)]);
        AstraQuestionRules.IsValidAnswer(Definition(Choice(type)), answer).Should().Be(valid);
    }

    [Theory]
    [InlineData("scale", 0, false)]
    [InlineData("scale", 1, true)]
    [InlineData("scale", 5, true)]
    [InlineData("scale", 6, false)]
    [InlineData("scale", 2.5, false)]
    [InlineData("slider", 0, true)]
    [InlineData("slider", 0.3, true)]
    [InlineData("slider", 1, true)]
    [InlineData("slider", 1.1, false)]
    [InlineData("slider", 0.35, false)]
    public void Numbers_EnforceBoundsAndExactSteps(string type, double number, bool valid)
    {
        var question = type == "scale" ? new AstraQuestion("q", type, "Energy?", LowLabel: "Low", HighLabel: "High")
            : new("q", type, "Duration?", Min: 0, Max: 1, Step: 0.1m, Unit: "hours");
        AstraQuestionRules.IsValidAnswer(Definition(question), new(Guid.NewGuid(), [], Answers: [new("q", Number: (decimal)number)]))
            .Should().Be(valid);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, true)]
    [InlineData(null, "Other", true)]
    [InlineData(null, null, false)]
    [InlineData(true, "Other", false)]
    public void YesNo_RequiresOneBooleanOrAllowedCustomAnswer(bool? value, string? text, bool valid)
    {
        var question = new AstraQuestion("q", "yesNo", "Ready?", AllowFreeText: true);
        AstraQuestionRules.IsValidAnswer(Definition(question), new(Guid.NewGuid(), [], Answers: [new("q", Boolean: value, FreeText: text)]))
            .Should().Be(valid);
    }

    [Fact]
    public void Replay_ComparesEveryTypedValueAndNormalizesOrder()
    {
        var block = IssuedAstraBlock.Create(Guid.NewGuid(), new("question", "", [], [], Questions:
            [Choice("multipleChoice"), new("ready", "yesNo", "Ready?"), new("duration", "slider", "Duration?", Min: 0, Max: 1, Step: 0.1m, Unit: "hours")]));
        var answer = new AstraBlockAnswer(block.Id, [], Answers: [new("q", ["b", "a"]), new("ready", Boolean: false), new("duration", Number: 0.3m)]);
        typeof(IssuedAstraBlock).GetProperty(nameof(block.AnswerJson))!.SetValue(block, IssuedAstraBlock.SerializeAnswer(answer));
        block.MatchesAnswer(answer with { Answers = [new("ready", Boolean: false), new("q", ["a", "b"]), new("duration", Number: 0.30m)] }).Should().BeTrue();
        block.MatchesAnswer(answer with { Answers = [new("q", ["a", "b"]), new("ready", Boolean: true), new("duration", Number: 0.3m)] }).Should().BeFalse();
        block.MatchesAnswer(answer with { Answers = [new("q", ["a", "b"]), new("ready", Boolean: false), new("duration", Number: 0.4m)] }).Should().BeFalse();
    }
}
