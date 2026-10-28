using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task PreviewDates_CurrentAccountCalendarOverridesOlderPreviewContext(string language)
    {
        ParentAccount(language);
        var localToday = new DateOnly(2026, 10, 28);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(localToday.AddDays(-1));
        var captured = GateSixCapture("composition-medical-batch-en-r1").GetProperty("toolCalls")[0];
        ScriptToolCall(captured.GetProperty("arguments").GetRawText(), "bulk_create_habits");
        var handler = CreateHandler();
        var older = await handler.Handle(new(_userId, "Prepare the medical appointments"), default);
        older.IsSuccess.Should().BeTrue(older.Error);
        var oldId = older.Value.PendingOperations!.Single().Id;
        var oldArguments = _pendingOperationStore.GetExecution(_userId, oldId)!.Arguments.GetRawText();
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(localToday);
        _promptBuilder.BuildDynamic(Arg.Any<Orbit.Domain.Interfaces.PromptBuildRequest>())
            .Returns(call => new SystemPromptBuilder().BuildDynamic(call.Arg<Orbit.Domain.Interfaces.PromptBuildRequest>()));
        var args = JsonNode.Parse(captured.GetProperty("arguments").GetRawText())!.AsObject();
        args["habits"]![0]!["due_date"] = "2026-10-27";
        args["habits"]![2]!["due_date"] = "2026-10-28";
        args["habits"]![2]!["start_date"] = "2026-10-28";
        args["orbit_write_complete"] = true;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AiToolRequest>();
                request.DynamicContext.Should().Contain("Today's Date: 2026-10-28")
                    .And.Contain("Yesterday: 2026-10-27").And.Contain("Tomorrow: 2026-10-29")
                    .And.Contain("Earlier messages and pending previews do not define today's date");
                return Result.Success(new AiResponse
                {
                    ToolCalls = [new("bulk_create_habits", "medical", JsonSerializer.SerializeToElement(args))],
                    ConversationContext = new()
                });
            });

        var result = await handler.Handle(new(_userId, language == "en"
            ? "The cholesterol exam was yesterday at 15:50. Start preparation today for the blood tests on 10/30 at 13:30."
            : "O exame de colesterol foi ontem às 15:50. Comece o preparo hoje para os exames de sangue de 30/10 às 13:30.",
            ClientContext: new(Locale: language, SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Id.Should().NotBe(oldId);
        pending.Items!.Select(item => item.Fields.Single(field => field.Field == "due_date").NewValue)
            .Should().Equal("2026-10-27", "2026-10-30", "2026-10-28", "2026-10-29", "2026-10-30");
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetProperty("habits");
        stored[0].GetProperty("due_date").GetString().Should().Be("2026-10-27");
        stored[2].GetProperty("due_date").GetString().Should().Be("2026-10-28");
        stored[1].GetProperty("due_date").GetString().Should().Be("2026-10-30");
        stored[2].GetProperty("end_date").GetString().Should().Be("2026-10-30");
        _pendingOperationStore.GetExecution(_userId, oldId)!.Arguments.GetRawText().Should().Be(oldArguments);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", 0)]
    [InlineData("pt-BR", 0)]
    [InlineData("en", 1)]
    [InlineData("pt-BR", 1)]
    public async Task PreviewIdentity_OnlyNamedOccurrenceSurvivesRevision(string language, int retainedIndex)
    {
        var habits = ParentAccount(language);
        ScriptToolCall("{}", "skip_habit");
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = habits.Select((habit, index) => new AiToolCall("skip_habit", "skip_" + index,
                    JsonSerializer.SerializeToElement(new { habit_id = habit.Id, orbit_write_complete = true }))).ToArray(),
                ConversationContext = new()
            }));
        var handler = CreateHandler();
        var first = await handler.Handle(new(_userId, "Skip both routines today"), default);
        first.IsSuccess.Should().BeTrue(first.Error);
        var original = first.Value.PendingOperations!.Single();
        var retained = original.Items!.Single(item => item.EntityId == habits[retainedIndex].Id);
        var removed = original.Items!.Single(item => item.EntityId == habits[1 - retainedIndex].Id);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AiToolRequest>();
                request.DynamicContext.Should().Contain("remove_item_ids contains the items to discard, never the items to keep")
                    .And.Contain("A request to skip a habit occurrence does not mean remove that habit's preview item");
                var declaration = request.ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value))
                    .Single(value => value.GetProperty("name").GetString() == "revise_pending_changes");
                declaration.GetProperty("parameters").GetProperty("properties").GetProperty("remove_item_ids")
                    .GetProperty("description").GetString().Should().Contain("Never list an item that should remain");
                var line = request.DynamicContext!.Split('\n').Single(value => value.StartsWith("Pending preview data: ", StringComparison.Ordinal));
                var previews = JsonDocument.Parse(line["Pending preview data: ".Length..]).RootElement;
                var preview = previews.EnumerateArray().Single(value => value.GetProperty("pending_operation_id").GetGuid() == original.Id);
                foreach (var item in original.Items!)
                {
                    var target = preview.GetProperty("items").EnumerateArray().Single(value => value.GetProperty("item_id").GetString() == item.ItemId);
                    item.EntityId.Should().NotBeNull();
                    target.GetProperty("entity_id").GetGuid().Should().Be(item.EntityId.GetValueOrDefault());
                    target.GetProperty("entity_name").GetString().Should().Be(item.EntityName);
                }
                return Result.Success(new AiResponse
                {
                    ToolCalls = [new("revise_pending_changes", "keep_named_occurrence", JsonSerializer.SerializeToElement(new
                    {
                        pending_operation_id = original.Id, items = Array.Empty<object>(),
                        remove_item_ids = new[] { removed.ItemId }, answer_question = false, orbit_read_complete = true
                    }))],
                    ConversationContext = new()
                });
            });

        var result = await handler.Handle(new(_userId, language == "en"
            ? $"For that preview, skip only {habits[retainedIndex].Title} today, do not delete it."
            : $"Nessa prévia, pula só {habits[retainedIndex].Title} hoje, não apaga.",
            ClientContext: new(Locale: language, SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var revised = result.Value.PendingOperations.Should().ContainSingle().Which;
        revised.Id.Should().Be(original.Id);
        var item = revised.Items.Should().ContainSingle().Which;
        item.EntityId.Should().Be(habits[retainedIndex].Id);
        item.EntityName.Should().Be(retained.EntityName);
        item.Fields.Should().Contain(field => field.Field == "action" && field.NewValue == (language == "en" ? "Skip" : "Pular"))
            .And.Contain(field => field.Field == "date" && field.NewValue == Today.ToString("yyyy-MM-dd"));
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Habits.Commands.SkipHabitCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task PreviewIdentity_FreshSkipKeepsAnUnrelatedPendingPreview(string language)
    {
        var habits = ParentAccount(language);
        ScriptToolCall(JsonSerializer.Serialize(new { habit_id = habits[0].Id, orbit_write_complete = true }), "skip_habit");
        var handler = CreateHandler();
        var first = await handler.Handle(new(_userId, "Skip Post Lunch today"), default);
        first.IsSuccess.Should().BeTrue(first.Error);
        var older = first.Value.PendingOperations!.Single();
        var original = _pendingOperationStore.GetExecution(_userId, older.Id)!.Arguments.GetRawText();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<AiToolRequest>().DynamicContext.Should()
                    .Contain("A fresh skip request uses skip_habit or bulk_skip_habits for the named habit ID");
                return Result.Success(new AiResponse
                {
                    ToolCalls = [new("skip_habit", "fresh_skip", JsonSerializer.SerializeToElement(new
                    {
                        habit_id = habits[1].Id, orbit_write_complete = true
                    }))],
                    ConversationContext = new()
                });
            });

        var result = await handler.Handle(new(_userId, language == "en"
            ? $"Uh, {habits[1].Title}, id {habits[1].Id}, I cannot today, skip just today, do not delete."
            : $"É, então, {habits[1].Title}, id {habits[1].Id}, hoje não vai dar não, pula só hoje tá, não apaga.",
            ClientContext: new(Locale: language, SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Id.Should().NotBe(older.Id);
        pending.Items.Should().ContainSingle().Which.EntityId.Should().Be(habits[1].Id);
        _pendingOperationStore.GetExecution(_userId, older.Id)!.Arguments.GetRawText().Should().Be(original);
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Habits.Commands.SkipHabitCommand>(), Arg.Any<CancellationToken>());
    }
}
