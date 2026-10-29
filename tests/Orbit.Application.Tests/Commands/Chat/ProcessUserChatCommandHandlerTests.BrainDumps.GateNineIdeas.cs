using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", 3, true)]
    [InlineData("pt-BR", 2, true)]
    [InlineData("pt-BR", 3, true)]
    [InlineData("en", 3, false)]
    [InlineData("pt-BR", 2, false)]
    [InlineData("pt-BR", 3, false)]
    public async Task BrainDump_GateNineDuplicateIdeaIsRegeneratedBeforeCombinedPreview(string language, int repeat, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 29));
        using var failed = GateNineIdeasCapture(language, repeat);
        using var passed = GateNineIdeasCapture(language, 1);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var duplicate = failed.RootElement.GetProperty("suggestionResult").GetProperty("drafts")[0]
            .Deserialize<HabitSuggestionDraft>(options)! with
        { Id = Guid.NewGuid() };
        var replacement = passed.RootElement.GetProperty("suggestionResult").GetProperty("drafts")[0]
            .Deserialize<HabitSuggestionDraft>(options)! with
        { Id = Guid.NewGuid() };
        var generator = Substitute.For<IHabitDraftGenerator>();
        generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>()).Returns(
            Result.Success<IReadOnlyList<HabitSuggestionDraft>>([duplicate]),
            Result.Success<IReadOnlyList<HabitSuggestionDraft>>([replacement]));
        var suggestions = new SuggestHabitDraftsQueryHandler(new HabitSuggestionService(_habitRepo, _goalRepo, _userDateService, generator));
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => suggestions.Handle(call.Arg<SuggestHabitDraftsQuery>(), call.Arg<CancellationToken>()));
        var suggest = new SuggestHabitsTool(_mediator);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(suggest, bulk);
        var held = HoldBrainDumpWithReads(bulk, suggest);
        var suggestionCall = BrainDumpCalls(failed).Single(call => call.Name == suggest.Name);
        var arguments = JsonNode.Parse(suggestionCall.Args.GetRawText())!.AsObject();
        arguments["excluded_activities"] = JsonSerializer.SerializeToNode(language == "en"
            ? new[] { "Call the dentist", "Stretch" } : new[] { "Ligar para o dentista", "Alongar" });
        SetupAiResponse(new AiResponse { ToolCalls = [suggestionCall with { Args = JsonSerializer.SerializeToElement(arguments) }], ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var result = call.Arg<IReadOnlyList<AiToolCallResult>>().Single(item => item.Name == suggest.Name);
                result.Success.Should().BeTrue(result.Error);
                var card = result.Payload.Should().BeOfType<HabitSuggestionsCard>().Subject;
                card.Drafts.Should().Equal(replacement);
                return Result.Success(new AiResponse
                {
                    ToolCalls = BrainDumpCalls(passed).Where(item => item.Name == bulk.Name).ToArray(),
                    ConversationContext = new()
                });
            });

        var response = await handler.Handle(new(UserId, "Please prepare the combined proposal.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true,
                SupportsHabitListCard: true, PartKinds: ["habitList", "habitSuggestions"], Locale: language)), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        await generator.Received(2).GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
        held.Should().ContainSingle();
        var preview = response.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items!.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        preview.Items![2].EntityName.Should().Be(replacement.Title);
        preview.Items[2].Fields.Should().ContainSingle(field => field.Field == "due_time" && field.NewValue == replacement.DueTime.ToString("HH:mm"));
        response.Value.Parts.Should().BeEmpty();
        response.Value.AiMessage.Should().BeNullOrEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    private static JsonDocument GateNineIdeasCapture(string language, int repeat) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrainDumps", $"g9d-brain-dump-ideas-{language}-r{repeat}.json")));
}
