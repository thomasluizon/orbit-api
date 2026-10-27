using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true, true, "en")]
    [InlineData(true, false, "en")]
    [InlineData(false, true, "en")]
    [InlineData(false, false, "en")]
    [InlineData(true, true, "pt-BR")]
    [InlineData(true, false, "pt-BR")]
    [InlineData(false, true, "pt-BR")]
    [InlineData(false, false, "pt-BR")]
    public async Task DaySummary_PreservesIndependentReadTextFallback(bool composition, bool summaryFirst, string language)
    {
        var user = Orbit.Domain.Entities.User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        if (composition) EnableComposition();
        var summary = SummaryReadTool();
        var goals = CompositionTool("query_goals", empty: true);
        var handler = CreateHandler(summary, goals);
        UseProductionReadExecutor(summary, goals);
        var summaryCall = new AiToolCall(summary.Name, "summary", ParseArguments("{}"));
        var goalsCall = new AiToolCall(goals.Name, "goals", ParseArguments("{}"));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = summaryFirst ? [summaryCall, goalsCall] : [goalsCall, summaryCall],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("You finished 99 habits.\n" + (summaryFirst
            ? "[[orbit:read:summary]]\n[[orbit:read:goals]]"
            : "[[orbit:read:goals]]\n[[orbit:read:summary]]"));

        var result = await handler.Handle(new(UserId, "Summarize my day and show my goals", ClientContext: new(
            SupportsParts: true, PartKinds: ["daySummary"], SupportsDaySummaryCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts.Should().NotBeNull();
        var parts = result.Value.Parts!;
        parts.OfType<ChatBlockPart>().Should().ContainSingle()
            .Which.Block.Should().BeOfType<DaySummaryCard>();
        var goalsText = language == "pt-BR" ? "Metas: 0" : "Goals: 0";
        parts.OfType<ChatTextPart>().Should().ContainSingle()
            .Which.Text.Should().Be(goalsText);
        result.Value.AiMessage.Should().Be(goalsText);
        parts[summaryFirst ? 0 : 1].Should().BeOfType<ChatBlockPart>();

        var stored = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        var summaryText = language == "pt-BR"
            ? "03/04/2026 a 03/04/2026: Concluídos / agendados: 0/0"
            : "04/03/2026 to 04/03/2026: Completed / scheduled: 0/0";
        var expected = summaryFirst ? new[] { summaryText, goalsText } : [goalsText, summaryText];
        foreach (var client in new AgentClientContext?[]
        {
            new(SupportsParts: true, PartKinds: []),
            new(SupportsParts: false),
            null
        })
        {
            var replay = ChatResponseNegotiation.ForClient(stored, client, [], language, user.TimeZone);

            replay.AiMessage.Should().Be(string.Join("\n\n", expected));
            replay.AiMessage.Should().NotContain("99").And.NotContain("daySummary")
                .And.NotContain("query_goals").And.NotMatchRegex(@"\d{4}-\d{2}-\d{2}");
            if (client?.SupportsParts == true)
                replay.Parts.Should().HaveCount(2).And.AllBeOfType<ChatTextPart>()
                    .And.Subject.Cast<ChatTextPart>().Select(part => part.Text).Should().Equal(expected);
            else replay.Parts.Should().BeNull();
        }
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
        if (composition)
        {
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("daySummary");
            result.Value.AiMessage.Should().BeNullOrEmpty();
            return;
        }
        result.Value.Parts.Should().HaveCount(2);
        var sentence = result.Value.Parts![0].Should().BeOfType<ChatTextPart>().Subject.Text;
        sentence.Should().Be(language == "pt-BR"
            ? "Aqui está o que você concluiu e o que falta para hoje."
            : "Here is what you finished and what is left for today.");
        sentence.Should().NotMatchRegex(@"\d");
        sentence.Count(c => c == '.').Should().Be(1);
        result.Value.Parts[1].Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("daySummary");
        result.Value.AiMessage.Should().Be(sentence);

        var stored = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        var replay = ChatResponseNegotiation.ForClient(stored, new(SupportsParts: true, PartKinds: []), [], language);

        replay.Parts.Should().HaveCount(2).And.AllBeOfType<ChatTextPart>();
        replay.Parts![0].Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(sentence);
        replay.AiMessage.Should().StartWith(sentence + "\n\n").And.Contain("0/0").And.NotContain("99");
    }
}
