using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "Choose a small first step.", false)]
    [InlineData("pt-BR", "Escolha um primeiro passo pequeno.", false)]
    [InlineData("en", "Choose a small first step.", true)]
    [InlineData("pt-BR", "Escolha um primeiro passo pequeno.", true)]
    public async Task G6_ModelCompositionEnvelopeNeverBecomesVisibleJson(string language, string text, bool fenced)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var envelope = JsonSerializer.Serialize(new
        {
            parts = new object[]
            {
                new { type = "text", text },
                new { type = "block", kind = "habitList", block = new { items = new[] { new { title = "Invented" } } } }
            },
            relatedActions = new[] { "Invented" }
        });
        SetupAiResponse(new AiResponse { TextMessage = fenced ? "```json\n" + envelope + "\n```" : envelope });
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new(UserId, "Help me get started.", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true, Locale: language),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(text);
        result.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(text));
        result.Value.RelatedSurfaces.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        AssertStoredComposition(result.Value);
    }

    [Theory]
    [InlineData("{\"parts\":[{\"type\":\"block\",\"kind\":\"habitList\",\"block\":{}}]}")]
    [InlineData("{\"parts\":[{\"type\":\"text\",\"text\":\"Unfinished")]
    [InlineData("{\"aiMessage\":{\"parts\":[]}}")]
    [InlineData("{\"parts\":null}")]
    [InlineData("{\"parts\":[{\"type\":\"text\",\"text\":{}}]}")]
    public async Task G6_InvalidModelCompositionCannotFabricateBlocksOrLeakJson(string envelope)
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse { TextMessage = envelope });

        var result = await CreateHandler().Handle(new(UserId, "Help me get started.", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>()
            .Which.Text.Should().NotContain("{").And.NotBeNullOrWhiteSpace();
        result.Value.AiMessage.Should().NotContain("{").And.NotBeNullOrWhiteSpace();
        AssertStoredComposition(result.Value);
    }

    [Theory]
    [InlineData("single-read-en-r1", false)]
    [InlineData("grounding-missing-pt-r1", true)]
    public async Task G6_CapturedSuccessfulHabitReadsKeepTheirTypedState(string capture, bool empty)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(empty ? [] : [CreateHabit("Water")]);
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan(capture));
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested entries.", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true, SupportsHabitListDoneStatus: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        var card = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<HabitListCard>().Subject;
        card.Items.Should().HaveCount(empty ? 0 : 1);
        card.Read!.Completeness.Should().Be("complete");
        card.Read.TotalCount.Should().Be(empty ? 0 : 1);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        if (empty) result.Value.RelatedSurfaces.Should().Contain("today");
        events.Should().NotContain(item => item.Type == "delta");
        await AssertNoCompositionContinuation();
        AssertStoredComposition(result.Value);
    }

    private void AssertStoredComposition(ChatResponse response)
    {
        var json = JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions);
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn => turn.ResponseJson == json));
        var stored = JsonSerializer.Deserialize<ChatResponse>(json, ChatStreamEvent.SerializerOptions)!;
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "recordList"],
            SupportsHabitListCard: true, SupportsRecordListCard: true, SupportsHabitListDoneStatus: true);
        var replay = ChatResponseNegotiation.ForClient(stored, client, ["astra_block_first"]);
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(response).ToJson());
    }
}
