using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("Suggest three habits for better sleep", "en", false)]
    [InlineData("Recommend habits for better sleep", "en", true)]
    [InlineData("Sugira três hábitos para dormir melhor", "pt-BR", false)]
    [InlineData("Quais hábitos você recomenda para dormir melhor?", "pt-BR", true)]
    public async Task HabitSuggestions_ProductionPathReturnsDraftBlockAndOneSentence(string message, string language, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var generator = Substitute.For<IHabitDraftGenerator>();
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), "📖", language == "en" ? "Read" : "Ler", "Habit",
            FrequencyUnit.Day, 1, [], Today, new(22, 0), new(22, 15), null, [],
            language == "en" ? "A quiet pause before bed." : "Uma pausa tranquila antes de dormir.");
        var drafts = new[]
        {
            draft,
            draft with { Id = Guid.NewGuid(), Title = language == "en" ? "Dim the lights" : "Diminuir a luz", DueTime = new(22, 15), DueEndTime = new(22, 20) },
            draft with { Id = Guid.NewGuid(), Title = language == "en" ? "Stretch" : "Alongar", DueTime = new(22, 20), DueEndTime = new(22, 30) }
        };
        generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitSuggestionDraft>>(drafts));
        var service = new HabitSuggestionService(_habitRepo, _goalRepo, _userDateService, generator);
        var queryHandler = new SuggestHabitDraftsQueryHandler(service);
        _mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<SuggestHabitDraftsQuery>(), call.Arg<CancellationToken>()));
        var tool = new SuggestHabitsTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "suggestions", JsonSerializer.Serialize(new { instruction = message, language, count = 3 })));
        ContinueWithReadDirective("Keep a consistent bedtime. Avoid caffeine. Spend 30 minutes winding down.");
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitSuggestions"]);
        var result = await handler.Handle(new(UserId, message, ClientContext: client), default);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        block.Kind.Should().Be("habitSuggestions");
        block.Block.Should().BeOfType<HabitSuggestionsCard>().Which.Drafts.Should().Equal(drafts);
        await generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request => request.Count == 3), Arg.Any<CancellationToken>());
        result.Value.Parts!.OfType<ChatTextPart>().Should().ContainSingle().Which.Text
            .Should().Be(HabitSuggestionsPresentation.Introduction(language));
        result.Value.AiMessage.Should().NotMatchRegex("[0-9]").And.NotContain("caffeine");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        var wire = JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions);
        var replay = JsonSerializer.Deserialize<ChatResponse>(wire, ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(ChatResponseNegotiation.ForClient(replay, client, [])).ToJson()
            .Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
        var legacy = ChatResponseNegotiation.ForClient(replay, null, []);
        legacy.Parts.Should().BeNull();
        legacy.AiMessage.Should().Contain(draft.Title).And.Contain(draft.Reason).And.Contain("22:00");
        var unsupported = ChatResponseNegotiation.ForClient(replay, client with { PartKinds = [] }, []);
        unsupported.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
    }
}
