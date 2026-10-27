using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Enums;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private static JsonElement GateSixCapture(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GateSix", name + ".json"))).RootElement.Clone();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GateSix_CompleteWriteRoundRetainsMedicalItemsAndAvoidPolarity(bool medical)
    {
        _user.SetLanguage(medical ? "en" : "pt-BR");
        ParentAccount(medical ? "en" : "pt-BR");
        var captured = GateSixCapture(medical ? "composition-medical-batch-en-r1" : "composition-avoid-form-pt-BR-r3");
        var first = captured.GetProperty("toolCalls")[0];
        var args = JsonNode.Parse(first.GetProperty("arguments").GetRawText())!.AsObject();
        args["orbit_write_complete"] = true;
        ScriptToolCall(args.ToJsonString(), first.GetProperty("name").GetString()!);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var preview = _pendingOperationStore.GetAwaiting(_userId).Single();
                var revision = medical
                    ? GateSixCapture("composition-medical-batch-pt-BR-r3").GetProperty("toolCalls")[1]
                    : captured.GetProperty("toolCalls")[1];
                var edits = JsonNode.Parse(revision.GetProperty("arguments").GetRawText())!.AsObject();
                edits["pending_operation_id"] = preview.PendingOperationId.ToString();
                return Result.Success(new AiResponse { ToolCalls = [new("revise_pending_changes", "unrequested", JsonSerializer.SerializeToElement(edits))], ConversationContext = new() });
            });

        var result = await CreateHandler().Handle(new(_userId, medical
            ? "10/26 cholesterol exam at 15:50 at home. 10/30 other blood tests at 13:30 at home. For the exam on 10/30: stop multivitamins three days before, skip Muay Thai 24 hours before and fast until collection."
            : "quero parar de fumar", ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(medical ? 5 : 1);
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.Arguments.GetRawText().Should().NotContain("orbit_write_complete");
        if (medical)
        {
            var items = stored.Arguments.GetProperty("habits");
            items[0].GetProperty("due_time").GetString().Should().Be("15:50");
            items[1].GetProperty("due_time").GetString().Should().Be("13:30");
            items[2].GetProperty("end_date").GetString().Should().Be("2026-10-30");
            items[3].GetProperty("due_date").GetString().Should().Be("2026-10-29");
        }
        else
        {
            stored.Arguments.GetProperty("title").GetString().Should().Be("Fumar");
            stored.Arguments.GetProperty("is_bad_habit").GetBoolean().Should().BeTrue();
        }
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task GateSix_ExistingRoutineWrappersOnlyProposeTheirChildren(string language)
    {
        var parents = ParentAccount(language);
        var captured = GateSixCapture("composition-existing-parents-" + language + "-r1");
        var args = JsonNode.Parse(captured.GetProperty("toolCalls")[0].GetProperty("arguments").GetRawText())!.AsObject();
        if (language == "pt-BR")
            for (var index = 0; index < 2; index++)
                parents[index].Update(new HabitUpdateParams(parents[index].Schedule, args["habits"]![index]!["title"]!.ToString(),
                    null, parents[index].FrequencyUnit, parents[index].FrequencyQuantity, [], false, Today));
        for (var index = 0; index < 2; index++)
            args["habits"]![index]!["habit_id"] = parents[index].Id.ToString();
        ScriptToolCall(args.ToJsonString(), "bulk_create_habits");
        var message = language == "en"
            ? "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)"
            : "Depois do almoço: tomar vitamina D (uma vez por semana) Antes de dormir: tomar rosuvastatina (todo dia)";

        var result = await CreateHandler().Handle(new(_userId, message), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(2).And.OnlyContain(item => item.EntityId == null);
        pending.Items!.Select(item => item.EntityName).Should().NotIntersectWith(parents.Select(parent => parent.Title));
        for (var index = 0; index < 2; index++)
        {
            pending.Items[index].Fields.Should().Contain(field => field.Field == "parent_habit_id"
                && field.ProposedValue!.Value.GetGuid() == parents[index].Id);
            pending.Items[index].Fields.Should().NotContain(field => field.Field == "sub_habits");
        }
        parents[0].Emoji.Should().BeNull();
        parents[1].Emoji.Should().BeNull();
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        for (var index = 0; index < 2; index++)
        {
            var parent = parents[index];
            var title = pending.Items[index].EntityName;
            await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.ParentHabitId == parent.Id
                && habit.Title == title), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task GateSix_RenameAndSkipUseOneApprovalWithAnExplicitSkipAction()
    {
        var habits = ParentAccount("en");
        ScriptToolCall("{}", "update_habit");
        var captured = GateSixCapture("compound-update-skip-en-r2");
        var calls = captured.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
        {
            var args = JsonNode.Parse(call.GetProperty("arguments").GetRawText())!.AsObject();
            args["habit_id"] = habits[index].Id.ToString();
            if (index == 1) args["date"] = Today.ToString("yyyy-MM-dd");
            return new AiToolCall(call.GetProperty("name").GetString()!, "capture_" + index, JsonSerializer.SerializeToElement(args));
        }).ToArray();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = new() }));

        var result = await CreateHandler().Handle(new(_userId, "Rename Post Lunch to Drink water and skip Before Bed today"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.OperationId.Should().Be(AccountChangeSet.OperationName);
        pending.Items.Should().HaveCount(2);
        pending.Items!.Single(item => item.EntityId == habits[1].Id).Fields.Should()
            .Contain(field => field.Field == "action" && field.NewValue == "Skip occurrence" && !field.IsEditable);
        habits[0].Title.Should().Be("Post Lunch");
        habits[1].NextDueDate.Should().Be(Today);
        _mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success("updated"));
        _mediator.Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new SkipHabitResponse(Guid.NewGuid())));
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        await _mediator.Received(1).Send(Arg.Is<SkipHabitCommand>(command => command.HabitId == habits[1].Id
            && command.Date == Today), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GateSix_SkipWithActiveStreakRewardsKeepsExternalEffectsOutsideAtomicBundles()
    {
        var habit = ParentAccount("en")[0];
        var goal = Goal.Create(new Goal.CreateGoalParams(_userId, "Keep a streak", 10, "days", Type: GoalType.Streak)).Value;
        habit.AddGoal(goal);
        var args = JsonSerializer.SerializeToElement(new { habit_id = habit.Id, date = Today.ToString("yyyy-MM-dd") });

        var supported = await AiToolAtomicity.SupportsAsync(new SkipHabitTool(_mediator, _habitRepo), _userId, args, default);

        supported.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>());
    }
}
