using FluentAssertions;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;
using Dto = Orbit.Infrastructure.Services.AiHabitSuggestionService.HabitSuggestionDto;

namespace Orbit.Infrastructure.Tests.Services;

public class AiHabitSuggestionServiceTests
{
    [Fact]
    public void BuildPrompt_English_ContainsTitleAndVocabulary()
    {
        var prompt = AiHabitSuggestionService.BuildPrompt("Run daily", "en");

        prompt.Should().Contain("Run daily");
        prompt.Should().Contain("English");
        prompt.Should().Contain("Day");
        prompt.Should().Contain("Week");
        prompt.Should().Contain("subHabits");
    }

    [Fact]
    public void BuildPrompt_Portuguese_RequestsBrazilianPortuguese()
    {
        AiHabitSuggestionService.BuildPrompt("Ler", "pt-BR")
            .Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void MapSuggestion_NullDto_ReturnsFailure()
    {
        var result = AiHabitSuggestionService.MapSuggestion(null);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void MapSuggestion_ValidDailyJson_MapsAllFields()
    {
        var dto = new Dto("R", "Day", 1, new[] { "Monday", "Wednesday" }, new[] { "Warm up", "Cool down" });

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.IsSuccess.Should().BeTrue();
        result.Value.Emoji.Should().Be("R");
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        result.Value.FrequencyQuantity.Should().Be(1);
        result.Value.Days.Should().BeEquivalentTo(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday });
        result.Value.SubHabits.Should().BeEquivalentTo(new[] { "Warm up", "Cool down" });
    }

    [Fact]
    public void MapSuggestion_WeeklyWithDays_StripsDays()
    {
        var dto = new Dto(null, "Week", 1, new[] { "Monday" }, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.Days.Should().BeEmpty();
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Week);
    }

    [Fact]
    public void MapSuggestion_DailyQuantityNotOne_StripsDays()
    {
        var dto = new Dto(null, "Day", 2, new[] { "Monday" }, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.Days.Should().BeEmpty();
        result.Value.FrequencyQuantity.Should().Be(2);
    }

    [Fact]
    public void MapSuggestion_OneTimeTask_NullsQuantityRegardlessOfModelValue()
    {
        var dto = new Dto("P", null, 5, null, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.FrequencyUnit.Should().BeNull();
        result.Value.FrequencyQuantity.Should().BeNull();
    }

    [Fact]
    public void MapSuggestion_RecurringWithoutQuantity_DefaultsToOne()
    {
        var dto = new Dto(null, "Week", null, null, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.FrequencyQuantity.Should().Be(1);
    }

    [Fact]
    public void MapSuggestion_TooManySubHabits_ClampsToCap()
    {
        var many = Enumerable.Range(1, 20).Select(i => $"Step {i}").ToArray();
        var dto = new Dto(null, null, null, null, many);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.SubHabits.Should().HaveCount(6);
    }

    [Fact]
    public void MapSuggestion_InvalidFrequencyUnit_BecomesNullAndDropsQuantity()
    {
        var dto = new Dto(null, "Fortnight", 3, null, new[] { "x" });

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.FrequencyUnit.Should().BeNull();
        result.Value.FrequencyQuantity.Should().BeNull();
    }

    [Fact]
    public void MapSuggestion_OverLongEmoji_Dropped()
    {
        var dto = new Dto(new string('x', 50), null, null, null, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.Emoji.Should().BeNull();
    }

    [Fact]
    public void MapSuggestion_DropsBlankAndOverLongSubHabitTitles()
    {
        var dto = new Dto(null, null, null, null, new[] { "  ", "ok", new string('a', 201) });

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.SubHabits.Should().BeEquivalentTo(new[] { "ok" });
    }

    [Fact]
    public void MapSuggestion_InvalidWeekdayNames_DroppedKeepingValidOnes()
    {
        var dto = new Dto(null, "Day", 1, new[] { "Monday", "Notaday", "monday" }, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.Days.Should().BeEquivalentTo(new[] { DayOfWeek.Monday });
    }
}
