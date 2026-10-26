using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task DaySummary_PreservesIndependentReadTextFallback(bool composition, bool summaryFirst)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var summary = SummaryReadTool();
        var goals = CompositionTool("query_goals");
        var handler = CreateHandler(summary, goals);
        UseProductionReadExecutor(summary, goals);
        var summaryCall = new AiToolCall(summary.Name, "summary", ParseArguments("{}"));
        var goalsCall = new AiToolCall(goals.Name, "goals", ParseArguments("{}"));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = summaryFirst ? [summaryCall, goalsCall] : [goalsCall, summaryCall],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective(summaryFirst
            ? "[[orbit:read:summary]]\n[[orbit:read:goals]]"
            : "[[orbit:read:goals]]\n[[orbit:read:summary]]");

        var result = await handler.Handle(new(UserId, "Summarize my day and show my goals", ClientContext: new(
            SupportsParts: true, PartKinds: ["daySummary"], SupportsDaySummaryCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().NotBeNull();
        var parts = result.Value.Parts!;
        parts.OfType<ChatBlockPart>().Should().ContainSingle()
            .Which.Block.Should().BeOfType<DaySummaryCard>();
        parts.OfType<ChatTextPart>().Should().ContainSingle()
            .Which.Text.Should().Be("0 goals");
        result.Value.AiMessage.Should().Be("0 goals");
        parts[summaryFirst ? 0 : 1].Should().BeOfType<ChatBlockPart>();
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task DaySummary_PutsOneLocalizedSentenceWithoutFiguresBeforeTheBlock(string language, bool composition)
    {
        var user = Orbit.Domain.Entities.User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        if (composition) EnableComposition();
        var tool = SummaryReadTool();
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "summary", "{}"));
        ContinueWithReadDirective("You finished 99 habits. Another sentence.\n[[orbit:read:summary]]");

        var result = await handler.Handle(new(UserId, "Summarize my day", ClientContext: new(
            SupportsParts: true, PartKinds: ["daySummary"], SupportsDaySummaryCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().HaveCount(2);
        var sentence = result.Value.Parts![0].Should().BeOfType<ChatTextPart>().Subject.Text;
        sentence.Should().Be(language == "pt-BR"
            ? "Aqui está o que você concluiu e o que falta para hoje."
            : "Here is what you finished and what is left for today.");
        sentence.Should().NotMatchRegex(@"\d");
        sentence.Count(c => c == '.').Should().Be(1);
        result.Value.Parts[1].Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("daySummary");
        result.Value.AiMessage.Should().Be(sentence);
    }
}
