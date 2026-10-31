using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task R9b_BrainDumpCompositionShowsIssuedCadenceClears(string language, bool oneTime)
    {
        SetupExistingParents(language);
        EnableComposition();
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
            language == "en" ? "Read" : "Ler", FrequencyUnit.Day, 1, Today,
            EndDate: Today.AddDays(30), Emoji: "📖")).Value;
        SetupGateTenHabits([existing]);
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), existing.Emoji!, existing.Title,
            oneTime ? "Task" : "Habit", oneTime ? null : FrequencyUnit.Day, oneTime ? null : 2, [],
            Today, new(8, 5), new(8, 10), null, [], "Adjust the cadence.", existing.Id);
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new HabitSuggestionsCard(language, [draft])));
        var suggest = new SuggestHabitsTool(_mediator);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(suggest, bulk);
        var held = HoldBrainDumpWithReads(bulk, suggest);
        using var capture = R9bCapture("brain-dump-ideas-pt-BR-r2");
        var calls = BrainDumpCalls(capture);
        SetupAiResponse(new() { ToolCalls = [calls.Single(call => call.Name == suggest.Name)], ConversationContext = new() });
        var create = calls.Single(call => call.Name == bulk.Name);
        var args = JsonNode.Parse(create.Args.GetRawText())!.AsObject();
        args["habits"]![2]!["title"] = draft.Title;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = [create with { Args = JsonSerializer.SerializeToElement(args) }], ConversationContext = new() }));

        var response = await handler.Handle(new(UserId, "Prepare the combined proposal.", ClientContext: new(
            SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["habitSuggestions"], Locale: language)), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        var bound = held.Should().ContainSingle().Which.Arguments;
        var preview = response.Value.PendingOperations.Should().ContainSingle().Which;
        var item = preview.Items![2];
        item.EntityId.Should().Be(existing.Id);
        var endDate = item.Fields.Should().ContainSingle(field => field.Field == "end_date").Which;
        endDate.OldValue.Should().Be(Today.AddDays(30).ToString("yyyy-MM-dd"));
        endDate.NewValue.Should().BeNull();
        endDate.ValueType.Should().Be("date");
        endDate.ProposedValue!.Value.ValueKind.Should().Be(JsonValueKind.Null);
        if (oneTime)
        {
            var frequency = item.Fields.Should().ContainSingle(field => field.Field == "frequency_unit").Which;
            frequency.OldValue.Should().Be("Day");
            frequency.NewValue.Should().BeNull();
            frequency.ProposedValue!.Value.ValueKind.Should().Be(JsonValueKind.Null);
        }
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo,
            _userDateService, new AiToolRegistry([bulk]));
        var single = (await previewer.PreviewAsync(UserId, "update_habit", bound.GetProperty("habits")[2])).Value!;
        foreach (var field in single.Items!.Single().Fields.Where(field => field.Field != "habit_id"))
        {
            item.Fields.Should().ContainSingle(candidate => candidate.Field == field.Field).Which.Should().BeEquivalentTo(field,
                options => options.Excluding(value => value.IsEditable).Excluding(value => value.ProposedValue));
            item.Fields.Single(candidate => candidate.Field == field.Field).ProposedValue!.Value.GetRawText()
                .Should().Be(field.ProposedValue!.Value.GetRawText());
        }
        preview.PreviewFingerprint.Should().Be(AgentOperationFingerprint.Compute(bulk.Name, JsonSerializer.Serialize(preview.Items)));
        var withoutEndClear = JsonNode.Parse(bound.GetRawText())!.AsObject();
        withoutEndClear["habits"]![2]!["clear_fields"] = oneTime ? new JsonArray("frequency_unit") : new JsonArray();
        var preserved = (await previewer.PreviewAsync(UserId, bulk.Name, JsonSerializer.SerializeToElement(withoutEndClear))).Value!;
        preserved.PreviewFingerprint.Should().NotBe(preview.PreviewFingerprint);
        preserved.Items![2].Fields.Should().NotContain(field => field.Field == "end_date");
        existing.EndDate.Should().Be(Today.AddDays(30));
        existing.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        existing.FrequencyQuantity.Should().Be(1);
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task R9b_BrainDumpCompositionRestoresTheIssuedDraftBeforePreview(string language, bool changedSchedule)
    {
        SetupExistingParents(language);
        EnableComposition();
        using var capture = R9bCapture("brain-dump-ideas-pt-BR-r2");
        var captured = capture.RootElement.GetProperty("suggestionResult").GetProperty("drafts")[0];
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), captured.GetProperty("emoji").GetString()!,
            captured.GetProperty("title").GetString()!, "Habit", FrequencyUnit.Day, 1, [],
            DateOnly.Parse(capture.RootElement.GetProperty("toolCalls")[2].GetProperty("arguments").GetProperty("habits")[2].GetProperty("due_date").GetString()!),
            new TimeOnly(8, 5), new TimeOnly(8, 10), null, [], captured.GetProperty("reason").GetString()!);
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new HabitSuggestionsCard(language, [draft])));
        var suggest = new SuggestHabitsTool(_mediator);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(suggest, bulk);
        var held = HoldBrainDumpWithReads(bulk, suggest);
        var calls = BrainDumpCalls(capture);
        SetupAiResponse(new() { ToolCalls = [calls.Single(call => call.Name == suggest.Name)], ConversationContext = new() });
        var create = calls.Single(call => call.Name == bulk.Name);
        var args = JsonNode.Parse(create.Args.GetRawText())!.AsObject();
        var idea = args["habits"]![2]!.AsObject();
        if (changedSchedule)
        {
            idea["due_time"] = "09:00";
            idea["due_end_time"] = "10:00";
            idea["frequency_quantity"] = 2;
            idea["days"] = new JsonArray("Monday");
            idea["emoji"] = "📌";
            idea["reminder_enabled"] = true;
            idea["reminder_times"] = new JsonArray(15);
            idea["checklist_items"] = JsonSerializer.SerializeToNode(new[] { new { text = "Changed", is_checked = false } });
        }
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = [create with { Args = JsonSerializer.SerializeToElement(args) }], ConversationContext = new() }));

        var response = await handler.Handle(new(UserId, "Prepare the combined proposal.", ClientContext: new(
            SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["habitSuggestions"], Locale: language)), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        held.Should().ContainSingle();
        var actual = held.Single().Arguments.GetProperty("habits")[2];
        var expected = PreviewHabitSuggestionCommandHandler.Arguments(draft, null);
        actual.TryGetProperty("reminder_enabled", out var enabled).Should().BeTrue("the issued draft explicitly disables reminders");
        enabled.GetBoolean().Should().BeFalse();
        foreach (var field in expected.EnumerateObject())
            actual.GetProperty(field.Name).GetRawText().Should().Be(field.Value.GetRawText(), field.Name);
        var preview = response.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items.Should().HaveCount(3);
        var fields = preview.Items![2].Fields;
        foreach (var name in new[] { "reminder_enabled", "reminder_times", "days", "checklist_items" })
            fields.Should().ContainSingle(field => field.Field == name).Which.ProposedValue!.Value.GetRawText()
                .Should().Be(expected.GetProperty(name).GetRawText());
        response.Value.Parts.Should().BeEmpty();
        response.Value.AiMessage.Should().BeNullOrEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
    }
}
