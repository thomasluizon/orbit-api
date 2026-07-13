using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;
using Dto = Orbit.Infrastructure.Services.AiHabitSuggestionService.HabitSuggestionDto;

namespace Orbit.Infrastructure.Tests.Services;

public class AiHabitSuggestionServiceTests
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] BrushAndShowerSubHabits = new[] { "Brush teeth", "Shower" };
    private static readonly string[] CheeseAndBreadChecklist = new[] { "Cheese", "Bread" };
    private static readonly string[] BrushAndMakeBedSubHabits = new[] { "Brush teeth", "Make bed" };
    private static readonly string[] MondayAndWednesdayNames = new[] { "Monday", "Wednesday" };
    private static readonly string[] WarmUpAndCoolDownSubHabits = new[] { "Warm up", "Cool down" };
    private static readonly string[] MondayName = new[] { "Monday" };
    private static readonly string[] PlaceholderSubHabits = new[] { "x" };
    private static readonly string[] WeekdayNamesWithInvalids = new[] { "Monday", "Notaday", "monday" };
    private static readonly string[] CheeseBreadEggsChecklist = new[] { "Cheese", "Bread", "Eggs" };
    private static readonly string[] OkSubHabits = new[] { "ok" };
    private static readonly string[] EggsChecklist = new[] { "Eggs" };
    private static readonly string[] BrushTeethSubHabits = new[] { "Brush teeth" };
    private static readonly string[] CheeseChecklist = new[] { "Cheese" };
    private static readonly DayOfWeek[] MondayAndWednesday = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };
    private static readonly DayOfWeek[] MondayOnly = new[] { DayOfWeek.Monday };

    private static Dto Deserialize(string json) => JsonSerializer.Deserialize<Dto>(json, DeserializeOptions)!;

    [Fact]
    public void Deserialize_SubHabitsAsStrings_ParsesEachString()
    {
        var dto = Deserialize("""{"subHabits":["Brush teeth","Shower"]}""");

        dto.SubHabits.Should().BeEquivalentTo(BrushAndShowerSubHabits);
    }

    [Fact]
    public void Deserialize_SubHabitsAsObjects_ExtractsTitle()
    {
        var dto = Deserialize("""{"subHabits":[{"title":"Brush teeth"},{"title":"Shower"}]}""");

        dto.SubHabits.Should().BeEquivalentTo(BrushAndShowerSubHabits);
    }

    [Fact]
    public void Deserialize_ChecklistItemsAsObjects_ExtractsName()
    {
        var dto = Deserialize("""{"checklistItems":[{"name":"Cheese"},{"name":"Bread"}]}""");

        dto.ChecklistItems.Should().BeEquivalentTo(CheeseAndBreadChecklist);
    }

    [Fact]
    public void Deserialize_MixedAndGarbageElements_KeepsUsableSkipsRest()
    {
        var dto = Deserialize("""{"subHabits":["Warm up",{"title":"Cardio"},42,null,{"foo":1},["nested"],{"activity":"Cool down"}]}""");

        dto.SubHabits.Should().Equal("Warm up", "Cardio", "Cool down");
    }

    [Fact]
    public void Deserialize_SubHabitsNotAnArray_YieldsEmptyList()
    {
        var dto = Deserialize("""{"subHabits":"just a string"}""");

        dto.SubHabits.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_ObjectSubHabits_ThenMapSuggestion_Succeeds()
    {
        var dto = Deserialize("""{"emoji":"🧼","frequencyUnit":"Day","frequencyQuantity":1,"subHabits":[{"title":"Brush teeth"},{"title":"Make bed"}]}""");

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.IsSuccess.Should().BeTrue();
        result.Value.SubHabits.Should().BeEquivalentTo(BrushAndMakeBedSubHabits);
    }

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
        var dto = new Dto("R", "Day", 1, MondayAndWednesdayNames, WarmUpAndCoolDownSubHabits);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.IsSuccess.Should().BeTrue();
        result.Value.Emoji.Should().Be("R");
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        result.Value.FrequencyQuantity.Should().Be(1);
        result.Value.Days.Should().BeEquivalentTo(MondayAndWednesday);
        result.Value.SubHabits.Should().BeEquivalentTo(WarmUpAndCoolDownSubHabits);
        result.Value.IsFlexible.Should().BeFalse();
        result.Value.FlexibleTarget.Should().BeNull();
        result.Value.DueTime.Should().BeNull();
        result.Value.ChecklistItems.Should().BeEmpty();
    }

    [Fact]
    public void MapSuggestion_WeeklyWithDays_StripsDays()
    {
        var dto = new Dto(null, "Week", 1, MondayName, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.Days.Should().BeEmpty();
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Week);
    }

    [Fact]
    public void MapSuggestion_DailyQuantityNotOne_StripsDays()
    {
        var dto = new Dto(null, "Day", 2, MondayName, null);

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
        var dto = new Dto(null, "Fortnight", 3, null, PlaceholderSubHabits);

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

        result.Value.SubHabits.Should().BeEquivalentTo(OkSubHabits);
    }

    [Fact]
    public void MapSuggestion_InvalidWeekdayNames_DroppedKeepingValidOnes()
    {
        var dto = new Dto(null, "Day", 1, WeekdayNamesWithInvalids, null);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.Days.Should().BeEquivalentTo(MondayOnly);
    }

    [Fact]
    public void BuildPrompt_DescribesFlexibleChecklistAndTimeFields()
    {
        var prompt = AiHabitSuggestionService.BuildPrompt("Go to the gym", "en");

        prompt.Should().Contain("isFlexible");
        prompt.Should().Contain("flexibleTarget");
        prompt.Should().Contain("dueTime");
        prompt.Should().Contain("checklistItems");
        prompt.Should().Contain("one-time");
    }

    [Fact]
    public void MapSuggestion_Flexible_KeepsTargetForcesQuantityOneAndStripsDays()
    {
        var dto = new Dto(null, "Week", 9, MondayName, null, IsFlexible: true, FlexibleTarget: 4);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.IsFlexible.Should().BeTrue();
        result.Value.FlexibleTarget.Should().Be(4);
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        result.Value.FrequencyQuantity.Should().Be(1);
        result.Value.Days.Should().BeEmpty();
    }

    [Fact]
    public void MapSuggestion_FlexibleWithoutPositiveTarget_DropsFlexible()
    {
        var dto = new Dto(null, "Week", 2, null, null, IsFlexible: true, FlexibleTarget: 0);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.IsFlexible.Should().BeFalse();
        result.Value.FlexibleTarget.Should().BeNull();
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        result.Value.FrequencyQuantity.Should().Be(2);
    }

    [Fact]
    public void MapSuggestion_FlexibleWithoutUnit_DropsFlexible()
    {
        var dto = new Dto(null, null, 5, null, null, IsFlexible: true, FlexibleTarget: 3);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.IsFlexible.Should().BeFalse();
        result.Value.FlexibleTarget.Should().BeNull();
        result.Value.FrequencyUnit.Should().BeNull();
        result.Value.FrequencyQuantity.Should().BeNull();
    }

    [Fact]
    public void MapSuggestion_DueTime_NormalizedToHoursAndMinutes()
    {
        var dto = new Dto(null, null, null, null, null, DueTime: "7:00");

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.DueTime.Should().Be("07:00");
    }

    [Fact]
    public void MapSuggestion_DueTime_StripsSeconds()
    {
        var dto = new Dto(null, null, null, null, null, DueTime: "20:30:45");

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.DueTime.Should().Be("20:30");
    }

    [Fact]
    public void MapSuggestion_DueTime_Invalid_Dropped()
    {
        var dto = new Dto(null, null, null, null, null, DueTime: "later");

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.DueTime.Should().BeNull();
    }

    [Fact]
    public void MapSuggestion_ChecklistItems_MappedWhenNoSubHabits()
    {
        var dto = new Dto(null, null, null, null, null, ChecklistItems: CheeseBreadEggsChecklist);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.ChecklistItems.Should().BeEquivalentTo(CheeseBreadEggsChecklist);
        result.Value.SubHabits.Should().BeEmpty();
    }

    [Fact]
    public void MapSuggestion_TooManyChecklistItems_ClampsToCap()
    {
        var many = Enumerable.Range(1, 20).Select(i => $"Item {i}").ToArray();
        var dto = new Dto(null, null, null, null, null, ChecklistItems: many);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.ChecklistItems.Should().HaveCount(6);
    }

    [Fact]
    public void MapSuggestion_DropsBlankAndOverLongChecklistItems()
    {
        var dto = new Dto(null, null, null, null, null, ChecklistItems: new[] { "  ", "Eggs", new string('a', 501) });

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.ChecklistItems.Should().BeEquivalentTo(EggsChecklist);
    }

    [Fact]
    public void MapSuggestion_SubHabitsAndChecklist_AreMutuallyExclusive_SubHabitsWin()
    {
        var dto = new Dto(null, null, null, null, BrushTeethSubHabits, ChecklistItems: CheeseChecklist);

        var result = AiHabitSuggestionService.MapSuggestion(dto);

        result.Value.SubHabits.Should().BeEquivalentTo(BrushTeethSubHabits);
        result.Value.ChecklistItems.Should().BeEmpty();
    }
}
