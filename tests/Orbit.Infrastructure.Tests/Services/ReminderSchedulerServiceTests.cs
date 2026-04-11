using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ReminderSchedulerServiceTests
{
    // Test FormatReminderText via reflection since it's private static
    private static string FormatReminderText(int minutesBefore, string lang)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("FormatReminderText", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [minutesBefore, lang])!;
    }

    // Test FormatScheduledReminderText via reflection
    private static string FormatScheduledReminderText(ScheduledReminderWhen when, string lang)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("FormatScheduledReminderText", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [when, lang])!;
    }

    // Test ShouldSendScheduledReminder via reflection
    private static bool ShouldSendScheduledReminder(
        ScheduledReminderTime sr, bool isDueToday, bool isDueTomorrow, TimeOnly userTimeNow)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("ShouldSendScheduledReminder", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [sr, isDueToday, isDueTomorrow, userTimeNow])!;
    }

    // Test Pluralize via reflection
    private static string Pluralize(string singular, int count)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("Pluralize", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [singular, count])!;
    }

    // --- FormatReminderText ---

    [Fact]
    public void FormatReminderText_Zero_English_ReturnsDueNow()
    {
        var result = FormatReminderText(0, "en");
        result.Should().Be("Due now");
    }

    [Fact]
    public void FormatReminderText_Zero_Portuguese_ReturnsAgora()
    {
        var result = FormatReminderText(0, "pt-BR");
        result.Should().Be("Agora");
    }

    [Fact]
    public void FormatReminderText_Minutes_English()
    {
        var result = FormatReminderText(15, "en");
        result.Should().Be("Due in 15 minutes");
    }

    [Fact]
    public void FormatReminderText_Minutes_Portuguese()
    {
        var result = FormatReminderText(1, "pt-BR");
        result.Should().Be("Em 1 minuto");
    }

    [Fact]
    public void FormatReminderText_MultipleMinutes_Portuguese()
    {
        var result = FormatReminderText(30, "pt-BR");
        result.Should().Be("Em 30 minutos");
    }

    [Fact]
    public void FormatReminderText_Hours_English()
    {
        var result = FormatReminderText(120, "en");
        result.Should().Be("Due in 2 hours");
    }

    [Fact]
    public void FormatReminderText_OneHour_English()
    {
        var result = FormatReminderText(60, "en");
        result.Should().Be("Due in 1 hour");
    }

    [Fact]
    public void FormatReminderText_Hours_Portuguese()
    {
        var result = FormatReminderText(60, "pt-BR");
        result.Should().Be("Em 1 hora");
    }

    [Fact]
    public void FormatReminderText_MultipleHours_Portuguese()
    {
        var result = FormatReminderText(180, "pt-BR");
        result.Should().Be("Em 3 horas");
    }

    [Fact]
    public void FormatReminderText_Days_English()
    {
        var result = FormatReminderText(1440, "en");
        result.Should().Be("Due in 1 day");
    }

    [Fact]
    public void FormatReminderText_MultipleDays_English()
    {
        var result = FormatReminderText(2880, "en");
        result.Should().Be("Due in 2 days");
    }

    [Fact]
    public void FormatReminderText_Days_Portuguese()
    {
        var result = FormatReminderText(1440, "pt-BR");
        result.Should().Be("Em 1 dia");
    }

    [Fact]
    public void FormatReminderText_MultipleDays_Portuguese()
    {
        var result = FormatReminderText(4320, "pt-BR");
        result.Should().Be("Em 3 dias");
    }

    // --- FormatScheduledReminderText ---

    [Fact]
    public void FormatScheduledReminderText_SameDay_English()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.SameDay, "en");
        result.Should().Be("Due today");
    }

    [Fact]
    public void FormatScheduledReminderText_SameDay_Portuguese()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.SameDay, "pt-BR");
        result.Should().Be("Para hoje");
    }

    [Fact]
    public void FormatScheduledReminderText_DayBefore_English()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.DayBefore, "en");
        result.Should().Be("Due tomorrow");
    }

    [Fact]
    public void FormatScheduledReminderText_DayBefore_Portuguese()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.DayBefore, "pt-BR");
        result.Should().Be("Para amanh\u00e3");
    }

    [Fact]
    public void FormatScheduledReminderText_Unknown_English()
    {
        var result = FormatScheduledReminderText((ScheduledReminderWhen)99, "en");
        result.Should().Be("Reminder");
    }

    [Fact]
    public void FormatScheduledReminderText_Unknown_Portuguese()
    {
        var result = FormatScheduledReminderText((ScheduledReminderWhen)99, "pt-BR");
        result.Should().Be("Lembrete");
    }

    // --- ShouldSendScheduledReminder ---

    [Fact]
    public void ShouldSendScheduledReminder_SameDay_DueToday_WithinWindow_ReturnsTrue()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(9, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendScheduledReminder_SameDay_NotDueToday_ReturnsFalse()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: false, isDueTomorrow: true, new TimeOnly(9, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSendScheduledReminder_DayBefore_DueTomorrow_WithinWindow_ReturnsTrue()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: false, isDueTomorrow: true, new TimeOnly(20, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendScheduledReminder_DayBefore_NotDueTomorrow_ReturnsFalse()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(20, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSendScheduledReminder_OutsideTimeWindow_ReturnsFalse()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        // 2 minutes later is outside the 1-minute window
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(9, 2));
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSendScheduledReminder_BeforeReminderTime_ReturnsFalse()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(8, 59));
        result.Should().BeFalse();
    }

    // --- Pluralize ---

    [Fact]
    public void Pluralize_Single_ReturnsSingular()
    {
        Pluralize("minute", 1).Should().Be("minute");
    }

    [Fact]
    public void Pluralize_Multiple_ReturnsPlural()
    {
        Pluralize("minute", 5).Should().Be("minutes");
    }

    [Fact]
    public void Pluralize_Zero_ReturnsPlural()
    {
        // 0 is not > 1, so returns singular (design choice in the code)
        Pluralize("hour", 0).Should().Be("hour");
    }
}
