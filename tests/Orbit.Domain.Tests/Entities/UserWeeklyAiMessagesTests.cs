using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserWeeklyAiMessagesTests
{
    [Theory]
    [InlineData(1, 0, "2026-08-03")]
    [InlineData(0, 1, "2026-08-02")]
    public void WeekStartPreferenceChange_PreservesCountUntilAnchoredBoundary(int originalStart, int newStart, string start)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetWeekStartDay(originalStart).IsSuccess.Should().BeTrue();
        var weekStart = DateOnly.Parse(start);
        user.IncrementAiMessageCount(weekStart);
        user.SetWeekStartDay(newStart).IsSuccess.Should().BeTrue();

        for (var day = 1; day < 7; day++)
        {
            user.GetAiMessagesUsedForQuota(weekStart.AddDays(day)).Should().Be(day);
            user.IncrementAiMessageCount(weekStart.AddDays(day));
            user.AiMessagesWeekStart.Should().Be(weekStart);
        }

        user.GetAiMessagesUsedForQuota(weekStart.AddDays(7)).Should().Be(0);
        user.IncrementAiMessageCount(weekStart.AddDays(7));
        var nextPeriod = AiAllowancePeriod.For(weekStart.AddDays(7), true, newStart);
        user.AiMessagesWeekStart.Should().Be(nextPeriod.Start);
        user.GetAiMessagesUsedForQuota(weekStart.AddDays(7)).Should().Be(1);
        user.GetAiMessagesUsedForQuota(nextPeriod.End).Should().Be(0);
    }

    [Theory]
    [InlineData(1, 0, "2026-08-03")]
    [InlineData(0, 1, "2026-08-02")]
    public void WeekStartPreferenceChange_RefundReleasesReservationWithinStoredWeek(int originalStart, int newStart, string start)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetWeekStartDay(originalStart).IsSuccess.Should().BeTrue();
        var weekStart = DateOnly.Parse(start);
        user.IncrementAiMessageCount(weekStart);
        user.IncrementAiMessageCount(weekStart.AddDays(6));
        user.SetWeekStartDay(newStart).IsSuccess.Should().BeTrue();

        user.RefundAiMessage(weekStart.AddDays(6));
        user.AiMessagesUsedThisWeek.Should().Be(1);
        user.AiMessagesUsedToday.Should().Be(0);
        user.RefundAiMessage(weekStart);
        user.AiMessagesUsedThisWeek.Should().Be(0);

        user.IncrementAiMessageCount(weekStart.AddDays(7));
        user.RefundAiMessage(weekStart);
        user.AiMessagesUsedThisWeek.Should().Be(1);
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
