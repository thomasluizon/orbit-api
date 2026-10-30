using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    public static IEnumerable<object[]> G15UnboundQuestions() =>
        from capture in new[] { "composition-choice-pt-BR-r1", "composition-choice-pt-BR-r3",
            "composition-proactive-pt-BR-r1", "composition-proactive-pt-BR-r2", "composition-proactive-pt-BR-r3",
            "composition-coaching-advice-pt-BR-r1", "composition-coaching-advice-pt-BR-r2", "composition-coaching-advice-pt-BR-r3" }
        from language in new[] { "en", "pt-BR" }
        select new object[] { capture, language };

    [Theory]
    [MemberData(nameof(G15UnboundQuestions))]
    public async Task G15_CapturedSetupQuestionsNeedATypedCreateResult(string capture, string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        SetupAiResponse(CapturedCompositionPlan("prod-r7-" + capture) with { ConversationContext = new() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = language == "en" ? "Start with one easy action." : "Comece com uma ação fácil." }));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice", "question"], Locale: language);

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        request.ToolDeclarations.Select(declaration => JsonSerializer.SerializeToElement(declaration).GetProperty("name").GetString())
            .Should().NotContain("ask");
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && results[0].Error!.Contains("typed missing-field result")), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en", "valid")]
    [InlineData("pt-BR", "valid")]
    [InlineData("en", "other-draft")]
    [InlineData("pt-BR", "other-draft")]
    [InlineData("en", "other-field")]
    [InlineData("pt-BR", "other-field")]
    [InlineData("en", "other-reason")]
    [InlineData("pt-BR", "other-reason")]
    [InlineData("en", "unbound-question")]
    [InlineData("pt-BR", "unbound-question")]
    public async Task G15_RealCreateMissingFieldDeclaresABoundSetupChoice(string language, string variant)
    {
        SetupExistingParents(language);
        EnableComposition();
        var create = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(create, RealAskTool());
        var title = language == "en" ? "Walking habit" : "Hábito de caminhada";
        SetupAiResponse(ToolResponse(create.Name, "new-draft", JsonSerializer.Serialize(new { title })) with { ConversationContext = new() });
        var rounds = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(invocation =>
            {
                if (rounds++ > 0) return Result.Success(new AiResponse { TextMessage = language == "en" ? "How often?" : "Com que frequência?" });
                var conversation = invocation.Arg<AiConversationContext>();
                var declaration = JsonSerializer.SerializeToElement(conversation.ToolDeclarationUpdates.Should().ContainSingle().Subject);
                declaration.GetProperty("name").GetString().Should().Be("ask");
                var forms = declaration.GetProperty("parameters").GetProperty("properties").GetProperty("input").GetProperty("anyOf");
                foreach (var form in forms.EnumerateArray())
                {
                    var fields = form.GetProperty("properties");
                    fields.GetProperty("missing_input").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Equal("habit_setup");
                    fields.GetProperty("draft_call_id").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Equal("new-draft");
                    fields.GetProperty("missing_field").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Equal("frequency_unit");
                    fields.TryGetProperty("proposed_habit_title", out _).Should().BeFalse();
                }
                var response = ToolResponse("ask", "setup", JsonSerializer.Serialize(new
                {
                    input = new
                    {
                        form = "choice",
                        missing_input = variant == "other-reason" ? "check_in" : "habit_setup",
                        draft_call_id = variant == "other-draft" ? "another-draft" : "new-draft",
                        missing_field = variant == "other-field" ? "due_time" : "frequency_unit",
                        question = language == "en" ? "How often?" : "Com que frequência?",
                        options = new[] { new { id = "day", label = language == "en" ? "Daily" : "Todo dia" }, new { id = "week", label = language == "en" ? "Weekly" : "Toda semana" } }
                    }
                }));
                if (variant == "unbound-question") response = ToolResponse("ask", "setup",
                    """{"input":{"form":"question","missing_input":"habit_setup","draft_call_id":"new-draft","missing_field":"frequency_unit","questions":[{"id":"unrelated","type":"yesNo","prompt":"Continue?"}]}}""");
                return Result.Success(response);
            });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice"], Locale: language);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (variant == "valid")
        {
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("choice");
            result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.NeedsClarification);
        }
        else
        {
            result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
            await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
            rounds.Should().Be(2);
        }
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        AssertStoredComposition(result.Value, client, language);
    }

    [Fact]
    public async Task G15_CapturedInitialTruncationCannotGenerateAnUnboundedDraftTitle()
    {
        SetupExistingParents("pt-BR");
        EnableComposition();
        var capture = CapturedCompositionPlan("prod-r7-composition-choice-pt-BR-r2");
        capture.ToolCalls.Should().BeEmpty();
        SetupAiResponse(capture with { IsTruncated = true });

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.", ClientContext: new(SupportsParts: true,
            PartKinds: ["choice", "question"], Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        JsonSerializer.Serialize(request.ToolDeclarations).Should().NotContain("proposed_habit_title");
        request.ToolDeclarations.Should().BeEmpty();
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await AssertNoCompositionContinuation();
    }
}
