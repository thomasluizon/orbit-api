using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Common;

public class AiAllowanceResetTests
{
    [Theory]
    [InlineData(1, 0, "2026-08-03", "America/Sao_Paulo", "2026-08-10T03:00:00Z", "2026-08-16T03:00:00Z")]
    [InlineData(0, 1, "2026-08-02", "Pacific/Auckland", "2026-08-08T12:00:00Z", "2026-08-09T12:00:00Z")]
    public void WeekStartPreferenceChange_ResetStaysAtAnchoredBoundary(int originalStart, int newStart, string start,
        string zone, string reset, string nextReset)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetTimeZone(zone).IsSuccess.Should().BeTrue();
        user.SetWeekStartDay(originalStart).IsSuccess.Should().BeTrue();
        var weekStart = DateOnly.Parse(start);
        user.IncrementAiMessageCount(weekStart);
        user.SetWeekStartDay(newStart).IsSuccess.Should().BeTrue();

        for (var day = 0; day < 7; day++)
            AiAllowanceReset.GetResetsAtUtc(user, weekStart.AddDays(day))
                .Should().Be(DateTimeOffset.Parse(reset).UtcDateTime);

        AiAllowanceReset.GetResetsAtUtc(user, weekStart.AddDays(7))
            .Should().Be(DateTimeOffset.Parse(nextReset).UtcDateTime);
    }

    [Theory]
    [InlineData(true, 0, "America/Sao_Paulo", "2026-08-02", "2026-08-09T03:00:00Z")]
    [InlineData(true, 1, "Pacific/Auckland", "2026-08-03", "2026-08-09T12:00:00Z")]
    [InlineData(true, 1, "America/New_York", "2026-03-02", "2026-03-09T04:00:00Z")]
    [InlineData(false, 1, "Pacific/Auckland", "2026-08-03", "2026-08-03T12:00:00Z")]
    [InlineData(false, 1, null, "2026-08-03", "2026-08-04T00:00:00Z")]
    [InlineData(true, 0, "America/Sao_Paulo", "2018-10-28", "2018-11-04T03:00:00Z")]
    public void Reset_UsesNextLocalPeriodBoundary(bool pro, int weekStartDay, string? zone, string today, string expected)
    {
        var user = User.Create("Reader", "reader@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        if (pro) user.GrantLifetimePro();
        if (zone is not null) user.SetTimeZone(zone).IsSuccess.Should().BeTrue();
        user.SetWeekStartDay(weekStartDay).IsSuccess.Should().BeTrue();

        var reset = AiAllowanceReset.GetResetsAtUtc(user, DateOnly.Parse(today));

        reset.Should().Be(DateTimeOffset.Parse(expected).UtcDateTime);
        reset.Kind.Should().Be(DateTimeKind.Utc);
    }
}
