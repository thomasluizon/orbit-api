using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public sealed class PendingOperationChangePreviewerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();

    [Fact]
    public async Task PreviewAsync_ThreeMatches_UsesEachRealOldEmoji()
    {
        var habits = new[] { CreateHabit("One", "🔴"), CreateHabit("Two", "🟡"), CreateHabit("Three", "🔵") };
        Setup(habits);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""");

        preview!.ChangeTargetCount.Should().Be(3);
        preview.Changes.Select(row => row.Field).Should().OnlyContain(field => field == "emoji");
        preview.Changes.Select(row => row.OldValue).Should().BeEquivalentTo(["🔴", "🟡", "🔵"]);
        preview.Changes.Select(row => row.NewValue).Should().OnlyContain(value => value == "✅");
    }

    [Fact]
    public async Task PreviewAsync_FortyMatches_ListsTenEntitiesAndTotal()
    {
        Setup(Enumerable.Range(1, 40).Select(index => CreateHabit($"Habit {index}", null)).ToArray());

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""");

        preview!.ChangeTargetCount.Should().Be(40);
        preview.Changes.Select(row => row.EntityId).Distinct().Should().HaveCount(10);
    }

    [Fact]
    public async Task PreviewAsync_Reschedule_ShowsOldAndNewDate()
    {
        Setup([CreateHabit("One", null)]);

        var preview = await Preview("bulk_reschedule_habits", """{"filter":{"all":true},"due_date":"2026-10-01"}""");

        preview!.Changes.Should().ContainSingle().Which.Should().Match<Orbit.Domain.Models.PendingOperationChange>(
            row => row.Field == "due_date" && row.OldValue == "2026-09-25" && row.NewValue == "2026-10-01");
    }

    [Fact]
    public async Task PreviewAsync_ZeroMatches_ReturnsEmptyRowsAndZeroTotal()
    {
        Setup([]);

        var preview = await Preview("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""");

        preview!.ChangeTargetCount.Should().Be(0);
        preview.Changes.Should().BeEmpty();
    }

    private async Task<Orbit.Domain.Models.PendingOperationChangePreview?> Preview(string operation, string json) =>
        await new PendingOperationChangePreviewer(_habits).PreviewAsync(
            UserId, operation, JsonDocument.Parse(json).RootElement);

    private void Setup(IReadOnlyList<Habit> habits) =>
        _habits.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
                Arg.Any<CancellationToken>())
            .Returns(habits);

    private static Habit CreateHabit(string title, string? emoji) =>
        Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25), Emoji: emoji)).Value;
}
