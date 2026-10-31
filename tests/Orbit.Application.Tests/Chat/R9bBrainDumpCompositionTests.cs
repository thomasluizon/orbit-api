using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
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
