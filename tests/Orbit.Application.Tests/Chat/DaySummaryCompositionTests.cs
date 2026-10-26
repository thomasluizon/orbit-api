using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
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
