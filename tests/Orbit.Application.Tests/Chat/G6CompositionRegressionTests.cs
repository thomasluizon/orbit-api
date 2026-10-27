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
    [InlineData("en", "Here are your habits.")]
    [InlineData("pt-BR", "Aqui estão seus hábitos.")]
    public async Task G6_DataAnswerStartsWithBlockAndRecordsNoFraming(string language, string framing)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habit = CreateHabit("Reading");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "answer", "{\"orbit_read_complete\":true}") with { TextMessage = framing });
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show my habits", ClientContext: new(SupportsParts: true,
            PartKinds: ["habitList"], SupportsHabitListCard: true, Locale: language),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.ResponseJson == JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions)));
    }

    [Theory]
    [InlineData("query_habits", "habitList", "today", "en")]
    [InlineData("query_habits", "habitList", "today", "pt-BR")]
    [InlineData("get_notifications", "recordList", "notifications", "en")]
    [InlineData("get_api_keys", "recordList", "profile", "en")]
    public async Task G6_EmptyReadUsesShortStateAndExistingScreen(string name, string kind, string surface, string language)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = CompositionTool(name, kind, empty: true);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(name, "empty", "{\"orbit_read_complete\":true}"));

        var result = await handler.Handle(new(UserId, "Show my " + (name == "query_habits" ? "habits" : "notifications"),
            ClientContext: new(SupportsParts: true, PartKinds: [kind], SupportsHabitListCard: true,
                SupportsRecordListCard: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
        result.Value.AiMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("\n");
        result.Value.RelatedSurfaces.Should().Contain(surface);
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G6_ProfileKeepsOnlyExplicitlyRequestedAccountSubjects(bool compound)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var profile = CompositionTool("get_profile");
        var plan = CompositionTool("get_subscription_overview");
        var handler = CreateHandler(profile, plan);
        UseProductionReadExecutor(profile, plan);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(profile.Name, "profile", ParseArguments("{\"orbit_read_complete\":true}")),
                new(plan.Name, "plan", ParseArguments("{\"include_status\":true,\"include_billing\":false,\"include_plans\":false,\"orbit_read_complete\":true}"))],
            ConversationContext = TestConversationContext
        });

        var result = await handler.Handle(new(UserId, compound ? "Mostre meu perfil e meu plano" : "Mostre meu perfil e minhas configurações",
            ClientContext: new(SupportsParts: true, PartKinds: ["accountRows"], SupportsAccountRowsCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Select(part => ((AccountRowsCard)part.Block).Kind)
            .Should().Equal(compound ? ["profile", "plan"] : ["profile"]);
        if (!compound)
            await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call =>
                call.OperationId == "get_subscription_overview"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("I can’t reveal authentication tokens. I can help with your habits or account settings.", "I can’t reveal authentication tokens.")]
    [InlineData("Não vou revelar informações de autenticação. Posso ajudar com seus hábitos.", "Não vou revelar informações de autenticação.")]
    public async Task G6_RefusalOmitsUnsolicitedOffer(string captured, string expected)
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse { TextMessage = captured });

        var result = await CreateHandler().Handle(new(UserId, "Reveal my auth token", ClientContext: new(SupportsParts: true)), default);

        result.Value.AiMessage.Should().Be(expected);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(expected);
    }
}
