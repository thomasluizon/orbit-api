using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    public static IEnumerable<object[]> G7SuggestionClients()
    {
        foreach (var language in new[] { "en", "pt-BR" })
            foreach (var composition in new[] { false, true })
                foreach (var streamed in new[] { false, true })
                    foreach (var capability in new[] { "supported", "legacy", "missing", "disabled" })
                        yield return [language, composition, streamed, capability];
    }

    [Theory]
    [MemberData(nameof(G7SuggestionClients))]
    public async Task G7_CapturedSuggestionsLeadWithBlockAndPreserveNegotiatedFallback(
        string language, bool composition, bool streamed, string capability)
    {
        SetupUserAndPayGate();
        var flags = new List<string> { FeatureFlagKeys.AstraConversationRecording };
        if (composition) flags.Add(FeatureFlagKeys.AstraBlockFirst);
        if (capability == "disabled") flags.Add(FeatureFlagKeys.AstraPartsDisabled);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(flags);
        var plan = CapturedCompositionPlan("composition-suggestions-duplicate-" + language + "-r1");
        var instruction = plan.ToolCalls!.Single().Args.GetProperty("instruction").GetString()!;
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), "📖", language == "en" ? "Read before bed" : "Ler antes de dormir",
            "Habit", FrequencyUnit.Day, 1, [], Today, new(22, 0), new(22, 15), null, [],
            language == "en" ? "A quiet pause before bed." : "Uma pausa tranquila antes de dormir.");
        var drafts = new[] { draft, draft with { Id = Guid.NewGuid(), Title = language == "en" ? "Water" : "Beber água",
            DueTime = new(9, 0), DueEndTime = new(9, 5) } };
        var generator = Substitute.For<IHabitDraftGenerator>();
        generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitSuggestionDraft>>(drafts));
        var service = new HabitSuggestionService(_habitRepo, _goalRepo, _userDateService, generator);
        var query = new SuggestHabitDraftsQueryHandler(service);
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => query.Handle(call.Arg<SuggestHabitDraftsQuery>(), call.Arg<CancellationToken>()));
        var tool = new SuggestHabitsTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(plan);
        ContinueWithReadDirective(string.Empty);
        var client = capability == "legacy" ? null : new AgentClientContext(SupportsParts: true,
            PartKinds: capability == "missing" ? [] : ["habitSuggestions"], Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, instruction, ClientContext: client,
            StreamSink: streamed ? item => { events.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var response = result.Value;
        response.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.PendingOperations.Should().BeNullOrEmpty();
        response.Actions.Should().BeEmpty();
        response.FollowUps.Should().BeNullOrEmpty();
        if (capability == "supported")
        {
            var block = response.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
            block.Kind.Should().Be("habitSuggestions");
            block.Block.Should().BeOfType<HabitSuggestionsCard>().Which.Drafts.Should().Equal(drafts);
            response.AiMessage.Should().BeNullOrEmpty();
            events.Should().NotContain(item => item.Type == "delta");
        }
        else
        {
            response.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
            response.AiMessage.Should().Be(HabitSuggestionsPresentation.Readable(new(language, drafts)));
            if (capability == "missing")
                response.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>()
                    .Which.Text.Should().Be(response.AiMessage);
            else
                response.Parts.Should().BeNull();
        }
        var wire = JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions);
        _ = _conversationRecorder.Received(1).RecordAsync(Arg.Is<ConversationTurnCapture>(turn => turn.ResponseJson == wire));
        var replay = JsonSerializer.Deserialize<ChatResponse>(wire, ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(ChatResponseNegotiation.ForClient(replay, client, flags)).ToJson()
            .Should().Be(ChatStreamEvent.Final(response).ToJson());
        await generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request =>
            request.Instruction == instruction && request.Language == language && request.Count == 2), Arg.Any<CancellationToken>());
        if (composition && capability is "supported" or "missing")
            await AssertNoCompositionContinuation();
    }
}
