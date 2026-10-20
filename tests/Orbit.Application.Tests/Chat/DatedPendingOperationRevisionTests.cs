using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class DatedPendingOperationRevisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviseDateRange_KeepingOneDate_PreservesTheOfferedItem(bool editDate)
    {
        using var context = new HeldWriteTestContext();
        context.Dates.GetUserTodayAsync(context.UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 10, 19));
        var other = Habit.Create(new HabitCreateParams(context.UserId, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2026, 9, 25))).Value;
        context.Records.Add(other);
        var tool = context.Tool("bulk_log_habits");
        var registry = new AiToolRegistry([tool]);
        var previewer = new PendingOperationChangePreviewer(context.Repository<Habit>(),
            context.Repository<Goal>(), context.Repository<Tag>(), context.Dates, registry);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habit_ids = new[] { context.Habit.Id, other.Id },
            dates = new[] { "2026-10-18", "2026-10-19" }
        });
        var original = await previewer.PreviewAsync(context.UserId, tool.Name, arguments);
        original!.Items.Should().HaveCount(4);
        var selected = original.Items!.Single(item => item.EntityId == context.Habit.Id
            && item.Fields.Single().NewValue == "2026-10-19");
        var pendingId = Guid.NewGuid();
        var current = new PendingAgentOperationExecution(pendingId, AgentCapabilityIds.HabitsBulkWrite,
            tool.Name, arguments, AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation,
            PreviewFingerprint: original.PreviewFingerprint);
        var store = Substitute.For<IPendingAgentOperationStore>();
        store.GetExecution(context.UserId, pendingId).Returns(_ => current);
        store.Revise(context.UserId, pendingId, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            current = current with
            {
                Arguments = JsonSerializer.Deserialize<JsonElement>(call.ArgAt<string>(3)),
                PreviewFingerprint = call.ArgAt<string>(5)
            };
            return true;
        });
        var service = new PendingOperationRevisionService(store, previewer,
            new RevisePendingOperationRequestValidator(), registry);
        var date = editDate ? "2026-10-17" : "2026-10-19";

        var result = await service.ReviseAsync(context.UserId, pendingId,
            new RevisePendingOperationRequest(original.PreviewFingerprint!,
                [new(selected.ItemId, editDate ? JsonSerializer.SerializeToElement(new { date }) : null)]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        var retained = result.Preview!.Items.Should().ContainSingle().Subject;
        retained.ItemId.Should().Be(selected.ItemId);
        retained.EntityId.Should().Be(context.Habit.Id);
        retained.Fields.Should().ContainSingle().Which.NewValue.Should().Be(date);
        current.Arguments.TryGetProperty("dates", out _).Should().BeFalse();
        current.Arguments.TryGetProperty("habit_ids", out _).Should().BeFalse();
        var stored = current.Arguments.GetProperty("revised_items").EnumerateArray().Should().ContainSingle().Subject;
        stored.GetProperty("habit_id").GetGuid().Should().Be(context.Habit.Id);
        stored.GetProperty("date").GetString().Should().Be(date);
        stored.GetProperty("preview_item_id").GetString().Should().Be(selected.ItemId);
        context.Commands.Should().BeEmpty();

        var refreshed = await service.RefreshAsync(context.UserId, pendingId, CancellationToken.None);

        refreshed.IsSuccess.Should().BeTrue(refreshed.Error);
        refreshed.Preview!.Items.Should().ContainSingle().Which.ItemId.Should().Be(selected.ItemId);
        (await service.IsCurrentAsync(context.UserId, current, CancellationToken.None)).Should().BeTrue();

        var editedAgain = await service.ReviseAsync(context.UserId, pendingId,
            new RevisePendingOperationRequest(refreshed.Preview.PreviewFingerprint!,
                [new(selected.ItemId, JsonSerializer.SerializeToElement(new { date = "2026-10-16" }))]),
            CancellationToken.None);

        editedAgain.IsSuccess.Should().BeTrue(editedAgain.Error);
        editedAgain.Preview!.Items.Should().ContainSingle().Which.ItemId.Should().Be(selected.ItemId);
        context.Commands.Should().BeEmpty();
        var execution = await tool.ExecuteAsync(current.Arguments, context.UserId, CancellationToken.None);

        execution.Success.Should().BeTrue(execution.Error);
        var command = context.Commands.Should().ContainSingle().Subject.Should().BeOfType<BulkLogHabitsCommand>().Subject;
        command.Items.Should().ContainSingle().Which.Should().Be(new BulkLogItem(context.Habit.Id, new DateOnly(2026, 10, 16)));
    }
}
