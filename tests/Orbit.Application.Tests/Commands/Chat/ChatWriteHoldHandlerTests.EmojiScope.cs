using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("add an icon to every habit without one", "{\"all\":true}", false)]
    [InlineData("coloca um ícone em todo hábito que não tem", "{\"all\":true}", false)]
    [InlineData("add an icon to every habit without one", "{\"has_emoji\":false}", false)]
    [InlineData("coloca um ícone em todo hábito que não tem", "{\"has_emoji\":false}", false)]
    [InlineData("change every icon", "{\"all\":true}", true)]
    [InlineData("troca todos os ícones", "{\"all\":true}", true)]
    public async Task Handle_EmojiScope_ApprovalAppliesOnlyPreviewedChanges(
        string message, string filter, bool replaceExisting)
    {
        var missing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read", FrequencyUnit.Day, 1, Today)).Value;
        var chosen = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Diet", FrequencyUnit.Day, 1, Today, Emoji: "🍔")).Value;
        var same = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Walk", FrequencyUnit.Day, 1, Today, Emoji: "📚")).Value;
        var habits = new[] { missing, chosen, same };
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _unitOfWork.PassThroughTransactions<int>();
        _emojiInference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success<IReadOnlyDictionary<Guid, string>>(
                call.ArgAt<IReadOnlyList<HabitEmojiInferenceInput>>(1).ToDictionary(input => input.HabitId, _ => "📚")));
        ScriptToolCall("{\"filter\":" + filter + ",\"infer_from_title\":true,\"replace_existing\":"
            + (replaceExisting ? "true" : "false") + "}", "bulk_update_habit_emojis");

        var held = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)), CancellationToken.None);

        held.IsSuccess.Should().BeTrue();
        var pending = held.Value.PendingOperations.Should().ContainSingle().Subject;
        var expectedIds = replaceExisting ? new[] { missing.Id, chosen.Id } : [missing.Id];
        pending.ChangeTargetCount.Should().Be(expectedIds.Length);
        pending.Items!.Select(item => item.EntityId!.Value).Should().BeEquivalentTo(expectedIds);
        pending.Items!.SelectMany(item => item.Fields).Should().OnlyContain(field => field.OldValue != field.NewValue);
        chosen.Emoji.Should().Be("🍔");
        missing.Emoji.Should().BeNull();
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        execution.Arguments.GetProperty("revised_items").EnumerateArray()
            .Select(item => item.GetProperty("habit_id").GetGuid()).Should().BeEquivalentTo(expectedIds);
        var late = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Late", FrequencyUnit.Day, 1, Today)).Value;
        habits = [missing, chosen, same, late];
        _emojiInference.ClearReceivedCalls();
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;

        var executed = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId,
            execution.OperationId, execution.Arguments, execution.Surface, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken));

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        missing.Emoji.Should().Be("📚");
        chosen.Emoji.Should().Be(replaceExisting ? "📚" : "🍔");
        same.Emoji.Should().Be("📚");
        late.Emoji.Should().BeNull();
        JsonSerializer.SerializeToElement(executed.Operation.Payload).GetProperty("applied_count")
            .GetInt32().Should().Be(expectedIds.Length);
        await _emojiInference.DidNotReceiveWithAnyArgs().InferAsync(default, default!, default);
    }
}
