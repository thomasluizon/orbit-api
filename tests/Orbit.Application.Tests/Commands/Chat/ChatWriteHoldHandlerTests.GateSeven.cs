using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private static readonly DateOnly GateSevenToday = new(2026, 10, 28);

    private Habit[] GateSevenAccount(string language)
    {
        ParentAccount(language);
        var titles = language == "en" ? new[] { "Water", "Walk" } : ["Beber água", "Caminhada"];
        var habits = titles.Select(title => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            _userId, title, FrequencyUnit.Day, 1, GateSevenToday)).Value).ToArray();
        UseParentHabits(habits);
        return habits;
    }

    private static JsonElement GateSevenCapture(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateSeven", name + ".json")));
        return document.RootElement.Clone();
    }

    private void ReplayGateSeven(string name, params Habit[] targets)
    {
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(GateSevenToday);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst]);
        var calls = GateSevenCapture(name).GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
        {
            var arguments = JsonNode.Parse(call.GetProperty("arguments").GetRawText())!.AsObject();
            var operation = call.GetProperty("name").GetString()!;
            if (arguments["habit_id"]?.ToString() == "[redacted]")
                arguments["habit_id"] = (operation == "update_habit" ? targets[0] : targets[^1]).Id.ToString();
            if (arguments["habit_ids"] is JsonArray ids)
            {
                ids.Count.Should().Be(targets.Length);
                for (var position = 0; position < ids.Count; position++)
                    ids[position] = targets[position].Id.ToString();
            }
            return new AiToolCall(operation, "gate_seven_" + index, JsonSerializer.SerializeToElement(arguments));
        }).ToArray();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = new() }));
    }

    [Fact]
    public async Task GateSeven_CapturedRenameAndSkipKeepBothActionsThroughOneApproval()
    {
        var habits = GateSevenAccount("en");
        ReplayGateSeven("compound-update-skip-en-r1", habits);

        var result = await CreateHandler().Handle(new(_userId, "Rename Water to Drink water, and skip Walk for today.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: "en")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(2);
        pending.Items!.Single(item => item.EntityId == habits[0].Id).Fields.Should()
            .Contain(field => field.Field == "title" && field.NewValue == "Drink water");
        var skip = pending.Items.Single(item => item.EntityId == habits[1].Id);
        skip.Fields.Should().Contain(field => field.Field == "action" && field.NewValue == "Skip"
            && field.ProposedValue!.Value.GetString() == "skip" && !field.IsEditable);
        skip.Fields.Should().Contain(field => field.Field == "date" && field.NewValue == "2026-10-28");
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        stored.OperationId.Should().Be(AccountChangeSet.OperationName);
        AccountChangeSet.Read(stored.Arguments)!.Select(member => member.OperationId).Should().Equal("update_habit", "skip_habit");
        stored.Arguments.GetRawText().Should().NotContain("orbit_write_complete");
        await _mediator.DidNotReceive().Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>());
        _mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success("updated"));
        _mediator.Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new SkipHabitResponse(Guid.NewGuid())));

        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        await _mediator.Received(1).Send(Arg.Is<SkipHabitCommand>(command => command.HabitId == habits[1].Id
            && command.Date == GateSevenToday), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(Arg.Is<UpdateHabitFieldsCommand>(command => command.HabitId == habits[0].Id), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("voice-pt-r1", false)]
    [InlineData("bulk-skip-pt-r1", true)]
    public async Task GateSeven_CapturedPortugueseSkipsKeepTheNamedActionAndDate(string capture, bool bulk)
    {
        var habits = GateSevenAccount("pt-BR");
        var targets = bulk ? habits : [habits[1]];
        ReplayGateSeven(capture, targets);

        var result = await CreateHandler().Handle(new(_userId, bulk
            ? "Pule Beber água e Caminhada hoje, em uma única operação em lote."
            : "pula só hoje tá, não apaga", ClientContext: new(SupportsParts: true,
                SupportsPendingOperationChanges: true, Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items!.Select(item => item.EntityId).Should().BeEquivalentTo(targets.Select(habit => (Guid?)habit.Id));
        pending.Items.Should().OnlyContain(item => item.RemovesData != true
            && item.Fields.Any(field => field.Field == "action" && field.NewValue == "Pular"
                && field.ProposedValue!.Value.GetString() == "skip")
            && item.Fields.Any(field => field.Field == "date" && field.NewValue == "2026-10-28"));
        _pendingOperationStore.GetExecution(_userId, pending.Id)!.OperationId.Should().Be(bulk ? "bulk_skip_habits" : "skip_habit");
        await _mediator.DidNotReceive().Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "composition-medical-batch-en-r2")]
    [InlineData("pt-BR", "composition-medical-batch-pt-BR-r1")]
    public async Task GateSeven_CapturedMedicalScheduleAndTitlesSurviveComposition(string language, string capture)
    {
        ParentAccount(language);
        ReplayGateSeven(capture);
        var capturedItems = GateSevenCapture(capture).GetProperty("toolCalls")[0].GetProperty("arguments").GetProperty("habits");

        var result = await CreateHandler().Handle(new(_userId, language == "en"
            ? "10/26 cholesterol exam at 15:50 at home. 10/30 other blood tests at 13:30 at home. For the exam on 10/30: don't take multivitamins three days before, don't go to Muay Thai 24 hours before and fast until collection."
            : "dia 26/10, exame de colesterol as 15h50, domiciliar dia 30/10, outros exames de sangue as 13h30, domiciliar PRO EXAME DO DIA 30: 3 dias antes, parar com multivitaminico 24 horas antes, nao ir pro muay thay ficar em jejum puro até a coleta",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(5);
        var items = _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetProperty("habits");
        for (var index = 0; index < 5; index++)
            foreach (var property in capturedItems[index].EnumerateObject().Where(property => property.Value.ValueKind != JsonValueKind.Null
                && property.Name is "title" or "frequency_unit" or "frequency_quantity" or "due_date" or "end_date"
                    or "due_time" or "due_end_time" or "is_bad_habit"))
            {
                items[index].TryGetProperty(property.Name, out var stored).Should().BeTrue(property.Name);
                JsonElement.DeepEquals(stored, property.Value).Should().BeTrue(property.Name);
            }
        items[0].GetProperty("due_date").GetString().Should().Be("2026-10-26");
        items[0].GetProperty("due_time").GetString().Should().Be("15:50");
        items[1].GetProperty("due_time").GetString().Should().Be("13:30");
        items[2].GetProperty("is_bad_habit").GetBoolean().Should().BeFalse();
        items[2].GetProperty("end_date").GetString().Should().Be("2026-10-30");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
