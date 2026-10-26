using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public sealed class HabitCalendarWindowTests
{
    [Theory]
    [InlineData(2024, 2, 29)]
    [InlineData(2025, 2, 28)]
    [InlineData(2026, 12, 31)]
    public void Resolve_UsesCompleteLocalMonth(int year, int month, int last)
    {
        var result = HabitCalendarWindow.Resolve(null, null, new(year, month, 15));
        result.Value.From.Should().Be(new DateOnly(year, month, 1));
        result.Value.To.Should().Be(new DateOnly(year, month, last));
    }

    [Theory]
    [InlineData(0, 61, true)]
    [InlineData(0, 62, false)]
    [InlineData(1, 0, false)]
    public void Create_GuardsOrderAndRange(int from, int to, bool valid)
    {
        var anchor = new DateOnly(2026, 1, 1);
        HabitCalendarWindow.Create(anchor.AddDays(from), anchor.AddDays(to)).IsSuccess.Should().Be(valid);
    }

    [Fact]
    public void Resolve_UsesRequestedMonthAndRejectsUnsafeLookback()
    {
        var result = HabitCalendarWindow.Resolve(null, new(2024, 2, 20), new(2026, 10, 1));
        result.Value.From.Should().Be(new DateOnly(2024, 2, 1));
        result.Value.To.Should().Be(new DateOnly(2024, 2, 20));
        HabitCalendarWindow.Create(DateOnly.MinValue, DateOnly.MinValue).IsFailure.Should().BeTrue();
    }
}
