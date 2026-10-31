using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", "composition-restart-approve-en-r3", false)]
    [InlineData("pt-BR", "composition-restart-approve-pt-BR-r1", false)]
    [InlineData("en", "composition-restart-preference-en-r1", true)]
    [InlineData("pt-BR", "composition-restart-preference-pt-BR-r2", true)]
    public async Task RestartPlan_CapturedSixHabitPlanAppliesExactlyTheShownDates(
        string language, string captureName, bool preference)
    {
        var capture = RestartRerunCapture(captureName);
        var habits = await RestartRerunAccountAsync(language, capture);
        var original = await RestartReadbackAsync();
        var pending = await ReplayRestartRerunAsync(language, capture, habits, preference);

        var shown = pending.Items!.ToDictionary(item => item.EntityId!.Value,
            item => DateOnly.ParseExact(item.Fields.Single().NewValue!, "yyyy-MM-dd"));
        shown.Values.Count(date => date == RestartRerunToday).Should().Be(2);
        shown.Values.Where(date => date != RestartRerunToday).Should()
            .OnlyContain(date => date > RestartRerunToday && date <= RestartRerunToday.AddDays(7));
        (await RestartReadbackAsync()).Should().BeEquivalentTo(original);

        await ApproveRestartRerunAsync(pending);

        var readback = await RestartReadbackAsync();
        foreach (var habit in habits)
            readback[habit.Id].Should().BeEquivalentTo(original[habit.Id] with { NextDueDate = shown[habit.Id] });
    }

    [Theory]
    [InlineData("en", "composition-restart-approve-en-r3", "composition-restart-approve-en-r1")]
    [InlineData("pt-BR", "composition-restart-approve-pt-BR-r1", "composition-restart-reject-pt-BR-r2")]
    public async Task RestartPlan_CancelledPlanCanBeReplacedByCapturedIndividualDates(
        string language, string firstCapture, string replacementCapture)
    {
        var first = RestartRerunCapture(firstCapture);
        var habits = await RestartRerunAccountAsync(language, first);
        var original = await RestartReadbackAsync();
        var pending = await ReplayRestartRerunAsync(language, first, habits);
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        _pendingOperationStore.Cancel(_userId, pending.Id,
            AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText())).Should().BeTrue();

        var replacement = await ReplayRestartRerunAsync(language, RestartRerunCapture(replacementCapture), habits,
            replacement: true);

        replacement.Id.Should().NotBe(pending.Id);
        replacement.Items!.Count(item => item.Fields.Single().NewValue == "2026-10-29").Should().Be(1);
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
        _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle()
            .Which.PendingOperationId.Should().Be(replacement.Id);
        (await RestartReadbackAsync()).Should().BeEquivalentTo(original);

        await ApproveRestartRerunAsync(replacement);

        var readback = await RestartReadbackAsync();
        foreach (var item in replacement.Items!)
            readback[item.EntityId!.Value].Should().BeEquivalentTo(original[item.EntityId.Value] with
            {
                NextDueDate = DateOnly.ParseExact(item.Fields.Single().NewValue!, "yyyy-MM-dd")
            });
    }

    private static readonly DateOnly RestartRerunToday = new(2026, 10, 29);

    private static JsonElement RestartRerunCapture(string name)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "RestartPlans", "g9a-" + name + ".json")));
        return capture.RootElement.Clone();
    }

    private async Task<Habit[]> RestartRerunAccountAsync(string language, JsonElement capture)
    {
        _user.SetLanguage(language);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(RestartRerunToday);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst]);
        var facts = capture.GetProperty("groundingEvidence").GetProperty("successfulTools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "query_habits").GetProperty("result");
        facts.GetProperty("date").GetString().Should().Be("2026-10-29");
        var habits = facts.GetProperty("items").EnumerateArray().Select(row => RestartHabit(
            row.GetProperty("title").GetString()!, DateOnly.ParseExact(row.GetProperty("startDate").GetString()!,
                "yyyy-MM-dd", CultureInfo.InvariantCulture))).ToArray();
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();
        SetupRestartRepository();
        return habits;
    }

    private async Task<PendingAgentOperation> ReplayRestartRerunAsync(string language, JsonElement capture,
        IReadOnlyList<Habit> habits, bool preference = false, bool replacement = false)
    {
        var calls = capture.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
        {
            var arguments = JsonNode.Parse(call.GetProperty("arguments").GetRawText())!.AsObject();
            if (arguments["revised_items"] is JsonArray items)
                for (var item = 0; item < items.Count; item++)
                    items[item]!["habit_id"] = habits[item].Id.ToString();
            return new AiToolCall(call.GetProperty("name").GetString()!, "restart-rerun-" + index,
                JsonSerializer.SerializeToElement(arguments));
        }).ToArray();
        AiResponse Response(AiToolCall call) => new() { ToolCalls = [call], ConversationContext = new() };
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(call =>
            {
                var declarations = JsonSerializer.SerializeToElement(call.Arg<AiToolRequest>().ToolDeclarations)
                    .EnumerateArray().ToArray();
                declarations.Should().NotContain(tool => tool.GetProperty("name").GetString() == "ask");
                declarations.Single(tool => tool.GetProperty("name").GetString() == "bulk_reschedule_habits")
                    .GetProperty("description").GetString().Should().Contain("strictly fewer")
                    .And.Contain("exactly").And.Contain("seven days");
                return Result.Success(Response(calls[0]));
            });
        var continuations = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ++continuations;
                var result = call.Arg<IReadOnlyList<AiToolCallResult>>().Should().ContainSingle().Which;
                result.Name.Should().Be("query_habits");
                result.Success.Should().BeTrue(result.Error);
                result.Payload.Should().BeOfType<HabitListCard>().Which.Items.Select(item => item.Id)
                    .Should().BeEquivalentTo(habits.Select(habit => habit.Id.ToString()));
                return Result.Success(Response(calls[1]));
            });
        var message = replacement
            ? language == "en" ? "I rejected that plan. Please propose a different restart plan."
                : "Rejeitei esse plano. Me proponha outro jeito de recomeçar."
            : language == "en" ? "I fell behind on my habits. Help me restart."
                : "Fiquei para trás nos meus hábitos. Me ajude a recomeçar.";
        if (preference)
            message += language == "en" ? " Only two things today." : " Só duas coisas hoje.";

        var planner = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(),
            Substitute.For<IIdempotencyStore>(), _unitOfWork);
        var tools = new AiToolRegistry([.. _toolRegistry.GetAll(),
            new AskTool(new BulkCreateHabitsTool(_mediator),
                new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService, planner),
                new BulkSkipHabitsTool(_mediator, _habitRepo, _userDateService, planner), _userDateService)]);
        var result = await CreateHandler(tools: tools).Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList", "choice", "pickList", "question"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        continuations.Should().Be(1, "the individual dates finish in a held preview without another model round");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.ActionKey.Should().Be("rescheduleHabits");
        pending.Items.Should().HaveCount(6);
        foreach (var (item, index) in habits.Select((habit, index) => (habit, index)))
        {
            var shown = pending.Items!.Single(row => row.EntityId == item.Id);
            shown.EntityName.Should().Be(item.Title);
            var date = shown.Fields.Should().ContainSingle().Which;
            date.Field.Should().Be("due_date");
            date.IsEditable.Should().BeTrue();
            date.NewValue.Should().Be(calls[1].Args.GetProperty("revised_items")[index]
                .GetProperty("updates").GetProperty("due_date").GetString());
        }
        return pending;
    }

    private async Task ApproveRestartRerunAsync(PendingAgentOperation pending)
    {
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.Arguments.TryGetProperty("revised_items", out _).Should().BeTrue();
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var applied = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken), default);
        applied.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(applied));
    }
}
