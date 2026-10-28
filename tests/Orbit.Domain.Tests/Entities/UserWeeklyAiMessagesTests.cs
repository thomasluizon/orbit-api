using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserWeeklyAiMessagesTests
{
    [Theory]
    [InlineData(1, 0, "2026-08-03", "2026-08-16")]
    [InlineData(0, 1, "2026-08-02", "2026-08-10")]
    public void WeekStartPreferenceChange_PreservesCountUntilAlignedBoundary(int originalStart, int newStart, string start, string end)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetWeekStartDay(originalStart).IsSuccess.Should().BeTrue();
        var weekStart = DateOnly.Parse(start);
        var periodEnd = DateOnly.Parse(end);
        user.IncrementAiMessageCount(weekStart);
        user.SetWeekStartDay(newStart).IsSuccess.Should().BeTrue();

        for (var day = 1; day < periodEnd.DayNumber - weekStart.DayNumber; day++)
        {
            var today = weekStart.AddDays(day);
            var period = AiAllowancePeriod.ActiveFor(today, true, newStart, user.AiMessagesWeekStart);
            period.Start.Should().Be(weekStart);
            period.End.Should().Be(periodEnd);
            period.Name.Should().Be("week");
            user.GetAiMessagesUsedForQuota(today).Should().Be(day);
            user.IncrementAiMessageCount(today);
            user.AiMessagesWeekStart.Should().Be(weekStart);
        }

        user.GetAiMessagesUsedForQuota(periodEnd).Should().Be(0);
        user.IncrementAiMessageCount(periodEnd);
        user.AiMessagesWeekStart.Should().Be(periodEnd);
        user.GetAiMessagesUsedForQuota(periodEnd).Should().Be(1);
        user.GetAiMessagesUsedForQuota(periodEnd.AddDays(7)).Should().Be(0);
    }

    [Theory]
    [InlineData(1, 0, "2026-08-03", "2026-08-16")]
    [InlineData(0, 1, "2026-08-02", "2026-08-10")]
    public void WeekStartPreferenceChange_RefundReleasesReservationWithinStoredWeek(int originalStart, int newStart, string start, string end)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetWeekStartDay(originalStart).IsSuccess.Should().BeTrue();
        var weekStart = DateOnly.Parse(start);
        var periodEnd = DateOnly.Parse(end);
        user.IncrementAiMessageCount(weekStart);
        user.SetWeekStartDay(newStart).IsSuccess.Should().BeTrue();
        user.IncrementAiMessageCount(weekStart.AddDays(7));

        user.RefundAiMessage(weekStart.AddDays(7));
        user.AiMessagesUsedThisWeek.Should().Be(1);
        user.AiMessagesUsedToday.Should().Be(0);
        user.RefundAiMessage(weekStart);
        user.AiMessagesUsedThisWeek.Should().Be(0);

        user.IncrementAiMessageCount(periodEnd);
        user.RefundAiMessage(weekStart);
        user.RefundAiMessage(periodEnd.AddDays(-1));
        user.AiMessagesUsedThisWeek.Should().Be(1);
    }

    [Theory]
    [InlineData(0, "2026-08-02")]
    [InlineData(1, "2026-08-03")]
    public void UnchangedWeekStartPreference_ActivePeriodsLastExactlySevenDays(int weekStartDay, string start)
    {
        var weekStart = DateOnly.Parse(start);
        for (var day = 0; day < 21; day++)
        {
            var period = AiAllowancePeriod.ActiveFor(weekStart.AddDays(day), true, weekStartDay, weekStart);

            period.Start.Should().Be(weekStart.AddDays(day / 7 * 7));
            period.End.Should().Be(period.Start.AddDays(7));
            period.Name.Should().Be("week");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Free_ActivePeriodRemainsOneDayWithStoredWeek(int weekStartDay)
    {
        var today = new DateOnly(2026, 8, 9);
        var period = AiAllowancePeriod.ActiveFor(today, false, weekStartDay, today.AddDays(-7));

        period.Start.Should().Be(today);
        period.End.Should().Be(today.AddDays(1));
        period.Name.Should().Be("day");
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    public void WeeklyCount_AccumulatesAcrossDaysAndResetsAtChosenWeekStart(int weekStartDay, int nextWeekDay)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetWeekStartDay(weekStartDay).IsSuccess.Should().BeTrue();
        var friday = new DateOnly(2026, 1, 2);
        for (var index = 0; index < 200; index++) user.IncrementAiMessageCount(friday);
        for (var index = 0; index < 50; index++) user.IncrementAiMessageCount(friday.AddDays(1));

        user.GetAiMessagesUsedForQuota(friday.AddDays(1)).Should().Be(250);
        user.GetAiMessagesUsedToday(friday.AddDays(1)).Should().Be(50);
        var nextWeek = friday.AddDays(nextWeekDay);
        user.GetAiMessagesUsedForQuota(nextWeek).Should().Be(0);
        user.IncrementAiMessageCount(nextWeek);
        user.GetAiMessagesUsedForQuota(nextWeek).Should().Be(1);
        user.GetAiMessagesUsedForQuota(friday).Should().Be(1);
    }

    [Fact]
    public void Refund_FromEarlierDayInSameWeek_ReleasesOnlyThatWeeksReservation()
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        var monday = new DateOnly(2026, 8, 3);
        user.IncrementAiMessageCount(monday);
        user.IncrementAiMessageCount(monday.AddDays(1));

        user.RefundAiMessage(monday);

        user.AiMessagesUsedThisWeek.Should().Be(1);
        user.AiMessagesUsedToday.Should().Be(1);
        user.IncrementAiMessageCount(monday.AddDays(7));
        user.RefundAiMessage(monday);
        user.AiMessagesUsedThisWeek.Should().Be(1);
        user.RefundAiMessage(monday.AddDays(7));
        user.RefundAiMessage(monday.AddDays(7));
        user.AiMessagesUsedThisWeek.Should().Be(0);
        user.AiMessagesUsedToday.Should().Be(0);
    }

    [Fact]
    public void PlanChanges_UseTheExistingDailyAndWeeklyCounts()
    {
        var user = User.Create("Reader", "reader@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        var monday = new DateOnly(2026, 8, 3);
        user.IncrementAiMessageCount(monday);
        user.IncrementAiMessageCount(monday.AddDays(1));
        user.GetAiMessagesUsedForQuota(monday.AddDays(1)).Should().Be(1);
        user.GrantLifetimePro();
        user.GetAiMessagesUsedForQuota(monday.AddDays(1)).Should().Be(2);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(7)]
    public void Period_RejectsInvalidWeekStart(int weekStartDay)
    {
        var create = () => AiAllowancePeriod.For(new DateOnly(2026, 8, 3), true, weekStartDay);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
