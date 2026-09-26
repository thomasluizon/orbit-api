using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class PendingOperationRevisionServiceTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IPendingAgentOperationStore _store = Substitute.For<IPendingAgentOperationStore>();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _dateService = Substitute.For<IUserDateService>();

    [Fact]
    public async Task ReviseAsync_RemovesOneItemAndEditsOnlyTheSelectedItem()
    {
        var first = CreateHabit("First");
        var second = CreateHabit("Second");
        SetupHabits([first, second]);
        const string json = "{\"filter\":{\"all\":true},\"updates\":{\"emoji\":\"A\"}}";
        var pendingId = Guid.NewGuid();
        var arguments = JsonDocument.Parse(json).RootElement.Clone();
        _store.GetExecution(_userId, pendingId).Returns(new PendingAgentOperationExecution(
            pendingId, AgentCapabilityIds.HabitsBulkWrite, "bulk_update_habits", arguments,
            AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation));
        _store.Revise(_userId, pendingId, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var previewer = new PendingOperationChangePreviewer(_habits, _dateService);
        var original = await previewer.PreviewAsync(_userId, "bulk_update_habits", arguments);
        var service = new PendingOperationRevisionService(_store, previewer,
            new RevisePendingOperationRequestValidator());
        using var edits = JsonDocument.Parse("{\"emoji\":\"B\"}");

        var result = await service.ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(original!.PreviewFingerprint!,
                [new RevisedPendingOperationItem(second.Id.ToString(), edits.RootElement.Clone())]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Preview!.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(second.Id);
        _store.Received(1).Revise(_userId, pendingId, Arg.Any<string>(),
            Arg.Is<string>(value => RevisedArgsMatch(value, second.Id, "B")),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("unlisted_item")]
    [InlineData("forbidden_field")]
    [InlineData("stale_preview")]
    public async Task ReviseAsync_RejectsUntrustedSelection(string caseName)
    {
        var habit = CreateHabit("First");
        SetupHabits([habit]);
        var pendingId = Guid.NewGuid();
        var arguments = JsonDocument.Parse("""{"filter":{"all":true},"updates":{"emoji":"A"}}""")
            .RootElement.Clone();
        _store.GetExecution(_userId, pendingId).Returns(new PendingAgentOperationExecution(
            pendingId, AgentCapabilityIds.HabitsBulkWrite, "bulk_update_habits", arguments,
            AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation));
        var previewer = new PendingOperationChangePreviewer(_habits, _dateService);
        var original = await previewer.PreviewAsync(_userId, "bulk_update_habits", arguments);
        var service = new PendingOperationRevisionService(_store, previewer,
            new RevisePendingOperationRequestValidator());
        using var edits = JsonDocument.Parse("{\"title\":\"Changed\"}");
        var item = new RevisedPendingOperationItem(
            caseName == "unlisted_item" ? Guid.NewGuid().ToString() : habit.Id.ToString(),
            caseName == "forbidden_field" ? edits.RootElement.Clone() : null);

        var result = await service.ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(
                caseName == "stale_preview" ? "old" : original!.PreviewFingerprint!, [item]),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _store.DidNotReceiveWithAnyArgs().Revise(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task ReviseAsync_EditsOneLogDateWithoutChangingAnother()
    {
        var first = CreateHabit("First");
        var second = CreateHabit("Second");
        SetupHabits([first, second]);
        var pendingId = Guid.NewGuid();
        var arguments = JsonDocument.Parse("""{"filter":{"all":true},"date":"2026-09-25"}""")
            .RootElement.Clone();
        _store.GetExecution(_userId, pendingId).Returns(new PendingAgentOperationExecution(
            pendingId, AgentCapabilityIds.HabitsBulkWrite, "bulk_log_habits", arguments,
            AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation));
        _store.Revise(_userId, pendingId, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var previewer = new PendingOperationChangePreviewer(_habits, _dateService);
        var preview = await previewer.PreviewAsync(_userId, "bulk_log_habits", arguments);
        using var edits = JsonDocument.Parse("{\"date\":\"2026-09-26\"}");
        var service = new PendingOperationRevisionService(_store, previewer,
            new RevisePendingOperationRequestValidator());

        var result = await service.ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(preview!.PreviewFingerprint!,
                [new(first.Id.ToString(), edits.RootElement.Clone()), new(second.Id.ToString())]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Received(1).Revise(_userId, pendingId, Arg.Any<string>(),
            Arg.Is<string>(json => DatesMatch(json, first.Id, second.Id)),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReviseAsync_CanEditRetainedCreateItemAgain()
    {
        var pendingId = Guid.NewGuid();
        var arguments = JsonDocument.Parse("""{"habits":[{"title":"One"},{"title":"Two"}]}""")
            .RootElement.Clone();
        var current = new PendingAgentOperationExecution(pendingId,
            AgentCapabilityIds.HabitsBulkWrite, "bulk_create_habits", arguments,
            AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation);
        _store.GetExecution(_userId, pendingId).Returns(_ => current);
        _store.Revise(_userId, pendingId, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
            {
                current = current with
                {
                    Arguments = JsonDocument.Parse(call.ArgAt<string>(3)).RootElement.Clone(),
                    PreviewFingerprint = call.ArgAt<string>(5)
                };
                return true;
            });
        var previewer = new PendingOperationChangePreviewer(_habits, _dateService);
        var service = new PendingOperationRevisionService(_store, previewer,
            new RevisePendingOperationRequestValidator());
        var original = await previewer.PreviewAsync(_userId, "bulk_create_habits", arguments);
        using var firstEdit = JsonDocument.Parse("{\"title\":\"Second\"}");

        var first = await service.ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(original!.PreviewFingerprint!,
                [new("1", firstEdit.RootElement.Clone())]), CancellationToken.None);
        using var secondEdit = JsonDocument.Parse("{\"title\":\"Final\"}");
        var second = await service.ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(first.Preview!.PreviewFingerprint!,
                [new("1", secondEdit.RootElement.Clone())]), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        current.Arguments.GetProperty("habits")[0].GetProperty("title").GetString()
            .Should().Be("Final");
        current.Arguments.GetProperty("habits")[0].GetProperty("preview_item_id").GetString()
            .Should().Be("1");
    }

    private void SetupHabits(IReadOnlyList<Habit> habits)
    {
        _dateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 9, 25));
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(habits);
    }

    private Habit CreateHabit(string title) => Habit.Create(new HabitCreateParams(
        _userId, title, FrequencyUnit.Day, 1, new DateOnly(2026, 9, 25))).Value;

    private static bool RevisedArgsMatch(string json, Guid id, string emoji)
    {
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("revised_items");
        return items.GetArrayLength() == 1
            && items[0].GetProperty("habit_id").GetString() == id.ToString()
            && items[0].GetProperty("updates").GetProperty("emoji").GetString() == emoji;
    }

    private static bool DatesMatch(string json, Guid first, Guid second)
    {
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("revised_items");
        return items.GetArrayLength() == 2
            && items[0].GetProperty("habit_id").GetString() == first.ToString()
            && items[0].GetProperty("date").GetString() == "2026-09-26"
            && items[1].GetProperty("habit_id").GetString() == second.ToString()
            && items[1].GetProperty("date").GetString() == "2026-09-25";
    }
}
