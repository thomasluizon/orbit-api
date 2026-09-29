using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class AgentArgumentPreviewTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IPendingAgentOperationStore _store = Substitute.For<IPendingAgentOperationStore>();
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goals = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Tag> _tags = Substitute.For<IGenericRepository<Tag>>();
    private readonly IUserDateService _dateService = Substitute.For<IUserDateService>();

    [Fact]
    public async Task PreviewAsync_HeldCreate_CarriesItemsAndFingerprint()
    {
        var preview = await Preview("create_habit",
            """{"title":"Beber agua","frequency_unit":"Day","frequency_quantity":1,"due_time":"08:00"}""");

        preview.Should().NotBeNull();
        preview!.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        preview.ChangeTargetCount.Should().Be(1);
        var item = preview.Items.Should().ContainSingle().Subject;
        item.EntityName.Should().Be("Beber agua");
        item.EntityId.Should().BeNull();
        item.Fields.Should().HaveCount(4);
        item.Fields.Should().OnlyContain(field => field.IsEditable);
        item.Fields.Should().Contain(field => field.Field == "title" && field.NewValue == "Beber agua"
            && field.OldValue == null);
        item.Fields.Should().Contain(field => field.Field == "due_time" && field.ValueType == "time");
    }

    [Fact]
    public async Task PreviewAsync_HeldUpdate_ShowsTheCurrentValueAndTheProposedValue()
    {
        var habit = Habit.Create(new HabitCreateParams(_userId, "Beber agua", FrequencyUnit.Day, 1,
            new DateOnly(2026, 9, 25))).Value;
        SetupHabit(habit);

        var preview = await Preview("update_habit",
            $$"""{"habit_id":"{{habit.Id}}","title":"Beber mais agua"}""");

        var item = preview!.Items.Should().ContainSingle().Subject;
        item.EntityId.Should().Be(habit.Id);
        item.EntityName.Should().Be("Beber agua");
        var title = item.Fields.Single(field => field.Field == "title");
        title.OldValue.Should().Be("Beber agua");
        title.NewValue.Should().Be("Beber mais agua");
        title.IsEditable.Should().BeTrue();
        item.Fields.Single(field => field.Field == "habit_id").IsEditable.Should().BeFalse();
    }

    [Fact]
    public async Task ReviseAsync_EditsTheHeldArguments()
    {
        var pendingId = Guid.NewGuid();
        var arguments = Parse("""{"title":"Beber agua","frequency_unit":"Day"}""");
        SetupExecution(pendingId, "create_habit", arguments);
        _store.Revise(_userId, pendingId, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var previewer = CreatePreviewer();
        var original = await previewer.PreviewAsync(_userId, "create_habit", arguments);
        using var edits = JsonDocument.Parse("""{"title":"Beber mais agua"}""");

        var result = await CreateService(previewer).ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(original!.PreviewFingerprint!,
                [new RevisedPendingOperationItem("0", edits.RootElement.Clone())]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _store.Received(1).Revise(_userId, pendingId, Arg.Any<string>(),
            Arg.Is<string>(value => ReadTitle(value) == "Beber mais agua"),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ReviseAsync_WithNoItems_CancelsTheHeldWrite()
    {
        var pendingId = Guid.NewGuid();
        var arguments = Parse("""{"title":"Beber agua","frequency_unit":"Day"}""");
        SetupExecution(pendingId, "create_habit", arguments);
        _store.Cancel(_userId, pendingId, Arg.Any<string>()).Returns(true);
        var previewer = CreatePreviewer();
        var original = await previewer.PreviewAsync(_userId, "create_habit", arguments);

        var result = await CreateService(previewer).ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(original!.PreviewFingerprint!, []),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Cancelled.Should().BeTrue();
        _store.DidNotReceiveWithAnyArgs().Revise(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task ReviseAsync_EditOfAFieldThatIsNotOffered_IsRejected()
    {
        var habit = Habit.Create(new HabitCreateParams(_userId, "Beber agua", FrequencyUnit.Day, 1,
            new DateOnly(2026, 9, 25))).Value;
        SetupHabit(habit);
        var pendingId = Guid.NewGuid();
        var arguments = Parse($$"""{"habit_id":"{{habit.Id}}","title":"Beber mais agua"}""");
        SetupExecution(pendingId, "update_habit", arguments);
        var previewer = CreatePreviewer();
        var original = await previewer.PreviewAsync(_userId, "update_habit", arguments);
        using var edits = JsonDocument.Parse($$"""{"habit_id":"{{Guid.NewGuid()}}"}""");

        var result = await CreateService(previewer).ReviseAsync(_userId, pendingId,
            new RevisePendingOperationRequest(original!.PreviewFingerprint!,
                [new RevisedPendingOperationItem(habit.Id.ToString(), edits.RootElement.Clone())]),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("field_not_offered");
        _store.DidNotReceiveWithAnyArgs().Revise(default, default, default!, default!, default!, default!);
    }

    private PendingOperationChangePreviewer CreatePreviewer() =>
        new(_habits, _goals, _tags, _dateService);

    private PendingOperationRevisionService CreateService(IPendingOperationChangePreviewer previewer) =>
        new(_store, previewer, new RevisePendingOperationRequestValidator());

    private Task<PendingOperationChangePreview?> Preview(string operationId, string json) =>
        CreatePreviewer().PreviewAsync(_userId, operationId, Parse(json));

    private void SetupExecution(Guid pendingId, string operationId, JsonElement arguments) =>
        _store.GetExecution(_userId, pendingId).Returns(new PendingAgentOperationExecution(
            pendingId, AgentCapabilityIds.HabitsWrite, operationId, arguments,
            AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation));

    private void SetupHabit(Habit habit) =>
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([habit]);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? ReadTitle(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("title").GetString();
    }
}
