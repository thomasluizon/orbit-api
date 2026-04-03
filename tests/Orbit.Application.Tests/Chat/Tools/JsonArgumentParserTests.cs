using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat.Tools;

public class JsonArgumentParserTests
{
    // --- GetOptionalString ---

    [Fact]
    public void GetOptionalString_ExistingProperty_ReturnsValue()
    {
        var el = Parse("""{"name": "Water"}""");
        JsonArgumentParser.GetOptionalString(el, "name").Should().Be("Water");
    }

    [Fact]
    public void GetOptionalString_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.GetOptionalString(el, "name").Should().BeNull();
    }

    [Fact]
    public void GetOptionalString_NullValue_ReturnsNull()
    {
        var el = Parse("""{"name": null}""");
        JsonArgumentParser.GetOptionalString(el, "name").Should().BeNull();
    }

    [Fact]
    public void GetOptionalString_NonStringValue_ReturnsNull()
    {
        var el = Parse("""{"name": 42}""");
        JsonArgumentParser.GetOptionalString(el, "name").Should().BeNull();
    }

    // --- GetOptionalInt ---

    [Fact]
    public void GetOptionalInt_ExistingProperty_ReturnsValue()
    {
        var el = Parse("""{"count": 5}""");
        JsonArgumentParser.GetOptionalInt(el, "count").Should().Be(5);
    }

    [Fact]
    public void GetOptionalInt_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.GetOptionalInt(el, "count").Should().BeNull();
    }

    [Fact]
    public void GetOptionalInt_NonNumberValue_ReturnsNull()
    {
        var el = Parse("""{"count": "five"}""");
        JsonArgumentParser.GetOptionalInt(el, "count").Should().BeNull();
    }

    // --- GetOptionalBool ---

    [Fact]
    public void GetOptionalBool_TrueValue_ReturnsTrue()
    {
        var el = Parse("""{"enabled": true}""");
        JsonArgumentParser.GetOptionalBool(el, "enabled").Should().BeTrue();
    }

    [Fact]
    public void GetOptionalBool_FalseValue_ReturnsFalse()
    {
        var el = Parse("""{"enabled": false}""");
        JsonArgumentParser.GetOptionalBool(el, "enabled").Should().BeFalse();
    }

    [Fact]
    public void GetOptionalBool_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.GetOptionalBool(el, "enabled").Should().BeNull();
    }

    [Fact]
    public void GetOptionalBool_NonBoolValue_ReturnsNull()
    {
        var el = Parse("""{"enabled": "yes"}""");
        JsonArgumentParser.GetOptionalBool(el, "enabled").Should().BeNull();
    }

    // --- ParseFrequencyUnit ---

    [Theory]
    [InlineData("Day", FrequencyUnit.Day)]
    [InlineData("Week", FrequencyUnit.Week)]
    [InlineData("Month", FrequencyUnit.Month)]
    [InlineData("Year", FrequencyUnit.Year)]
    public void ParseFrequencyUnit_ValidValues_ReturnsEnum(string value, FrequencyUnit expected)
    {
        var el = Parse($$$"""{"frequency_unit": "{{{value}}}"}""");
        JsonArgumentParser.ParseFrequencyUnit(el).Should().Be(expected);
    }

    [Fact]
    public void ParseFrequencyUnit_CaseInsensitive_Works()
    {
        var el = Parse("""{"frequency_unit": "day"}""");
        JsonArgumentParser.ParseFrequencyUnit(el).Should().Be(FrequencyUnit.Day);
    }

    [Fact]
    public void ParseFrequencyUnit_InvalidValue_ReturnsNull()
    {
        var el = Parse("""{"frequency_unit": "Invalid"}""");
        JsonArgumentParser.ParseFrequencyUnit(el).Should().BeNull();
    }

    [Fact]
    public void ParseFrequencyUnit_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.ParseFrequencyUnit(el).Should().BeNull();
    }

    // --- ParseDays ---

    [Fact]
    public void ParseDays_ValidDays_ReturnsDayList()
    {
        var el = Parse("""{"days": ["Monday", "Wednesday", "Friday"]}""");
        var days = JsonArgumentParser.ParseDays(el);
        days.Should().NotBeNull();
        days.Should().HaveCount(3);
        days.Should().Contain(DayOfWeek.Monday);
        days.Should().Contain(DayOfWeek.Wednesday);
        days.Should().Contain(DayOfWeek.Friday);
    }

    [Fact]
    public void ParseDays_EmptyArray_ReturnsNull()
    {
        var el = Parse("""{"days": []}""");
        JsonArgumentParser.ParseDays(el).Should().BeNull();
    }

    [Fact]
    public void ParseDays_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.ParseDays(el).Should().BeNull();
    }

    [Fact]
    public void ParseDays_InvalidDayNames_ReturnsNull()
    {
        var el = Parse("""{"days": ["Funday"]}""");
        JsonArgumentParser.ParseDays(el).Should().BeNull();
    }

    // --- ParseDateOnly ---

    [Fact]
    public void ParseDateOnly_ValidDate_ReturnsDate()
    {
        var el = Parse("""{"due_date": "2026-04-03"}""");
        JsonArgumentParser.ParseDateOnly(el, "due_date").Should().Be(new DateOnly(2026, 4, 3));
    }

    [Fact]
    public void ParseDateOnly_InvalidFormat_ReturnsNull()
    {
        var el = Parse("""{"due_date": "04/03/2026"}""");
        JsonArgumentParser.ParseDateOnly(el, "due_date").Should().BeNull();
    }

    [Fact]
    public void ParseDateOnly_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.ParseDateOnly(el, "due_date").Should().BeNull();
    }

    // --- ParseTimeOnly ---

    [Fact]
    public void ParseTimeOnly_ValidTime_ReturnsTime()
    {
        var el = Parse("""{"due_time": "14:30"}""");
        JsonArgumentParser.ParseTimeOnly(el, "due_time").Should().Be(new TimeOnly(14, 30));
    }

    [Fact]
    public void ParseTimeOnly_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.ParseTimeOnly(el, "due_time").Should().BeNull();
    }

    // --- ParseIntArray ---

    [Fact]
    public void ParseIntArray_ValidArray_ReturnsInts()
    {
        var el = Parse("""{"reminder_times": [15, 30, 60]}""");
        var result = JsonArgumentParser.ParseIntArray(el, "reminder_times");
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { 15, 30, 60 });
    }

    [Fact]
    public void ParseIntArray_EmptyArray_ReturnsNull()
    {
        var el = Parse("""{"reminder_times": []}""");
        JsonArgumentParser.ParseIntArray(el, "reminder_times").Should().BeNull();
    }

    [Fact]
    public void ParseIntArray_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.ParseIntArray(el, "reminder_times").Should().BeNull();
    }

    // --- ParseChecklistItems ---

    [Fact]
    public void ParseChecklistItems_ValidItems_ReturnsList()
    {
        var el = Parse("""{"checklist_items": [{"text": "Item 1"}, {"text": "Item 2", "is_checked": true}]}""");
        var result = JsonArgumentParser.ParseChecklistItems(el);
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result![0].Text.Should().Be("Item 1");
        result[0].IsChecked.Should().BeFalse();
        result[1].Text.Should().Be("Item 2");
        result[1].IsChecked.Should().BeTrue();
    }

    [Fact]
    public void ParseChecklistItems_EmptyArray_ReturnsNull()
    {
        var el = Parse("""{"checklist_items": []}""");
        JsonArgumentParser.ParseChecklistItems(el).Should().BeNull();
    }

    [Fact]
    public void ParseChecklistItems_MissingText_SkipsItem()
    {
        var el = Parse("""{"checklist_items": [{"text": ""}, {"text": "Valid"}]}""");
        var result = JsonArgumentParser.ParseChecklistItems(el);
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result![0].Text.Should().Be("Valid");
    }

    // --- ParseScheduledReminders ---

    [Fact]
    public void ParseScheduledReminders_ValidItems_ReturnsList()
    {
        var el = Parse("""{"scheduled_reminders": [{"when": "same_day", "time": "09:00"}, {"when": "day_before", "time": "20:00"}]}""");
        var result = JsonArgumentParser.ParseScheduledReminders(el);
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result![0].When.Should().Be(ScheduledReminderWhen.SameDay);
        result[0].Time.Should().Be(new TimeOnly(9, 0));
        result[1].When.Should().Be(ScheduledReminderWhen.DayBefore);
    }

    [Fact]
    public void ParseScheduledReminders_InvalidWhen_SkipsItem()
    {
        var el = Parse("""{"scheduled_reminders": [{"when": "invalid", "time": "09:00"}, {"when": "same_day", "time": "10:00"}]}""");
        var result = JsonArgumentParser.ParseScheduledReminders(el);
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
    }

    [Fact]
    public void ParseScheduledReminders_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.ParseScheduledReminders(el).Should().BeNull();
    }

    // --- TryParseScheduledReminderWhen ---

    [Theory]
    [InlineData("same_day", ScheduledReminderWhen.SameDay, true)]
    [InlineData("day_before", ScheduledReminderWhen.DayBefore, true)]
    [InlineData("invalid", default(ScheduledReminderWhen), false)]
    public void TryParseScheduledReminderWhen_HandlesValues(string input, ScheduledReminderWhen expectedWhen, bool expectedResult)
    {
        var success = JsonArgumentParser.TryParseScheduledReminderWhen(input, out var result);
        success.Should().Be(expectedResult);
        if (expectedResult)
            result.Should().Be(expectedWhen);
    }

    // --- PropertyExists ---

    [Fact]
    public void PropertyExists_ExistingProperty_ReturnsTrue()
    {
        var el = Parse("""{"name": "test"}""");
        JsonArgumentParser.PropertyExists(el, "name").Should().BeTrue();
    }

    [Fact]
    public void PropertyExists_MissingProperty_ReturnsFalse()
    {
        var el = Parse("{}");
        JsonArgumentParser.PropertyExists(el, "name").Should().BeFalse();
    }

    [Fact]
    public void PropertyExists_NullValue_ReturnsTrue()
    {
        var el = Parse("""{"name": null}""");
        JsonArgumentParser.PropertyExists(el, "name").Should().BeTrue();
    }

    // --- GetNullableString ---

    [Fact]
    public void GetNullableString_StringValue_ReturnsString()
    {
        var el = Parse("""{"name": "test"}""");
        JsonArgumentParser.GetNullableString(el, "name").Should().Be("test");
    }

    [Fact]
    public void GetNullableString_NullValue_ReturnsNull()
    {
        var el = Parse("""{"name": null}""");
        JsonArgumentParser.GetNullableString(el, "name").Should().BeNull();
    }

    [Fact]
    public void GetNullableString_MissingProperty_ReturnsNull()
    {
        var el = Parse("{}");
        JsonArgumentParser.GetNullableString(el, "name").Should().BeNull();
    }

    // --- ParseTimeOnlyFromString ---

    [Fact]
    public void ParseTimeOnlyFromString_ValidTime_ReturnsTime()
    {
        JsonArgumentParser.ParseTimeOnlyFromString("14:30").Should().Be(new TimeOnly(14, 30));
    }

    [Fact]
    public void ParseTimeOnlyFromString_InvalidTime_ReturnsNull()
    {
        JsonArgumentParser.ParseTimeOnlyFromString("not-a-time").Should().BeNull();
    }

    // --- Helper ---

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}
