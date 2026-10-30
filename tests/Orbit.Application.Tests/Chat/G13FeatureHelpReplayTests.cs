using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task G13_CapturedHelpSentencesFitShortLinesWithoutLosingContent(int repeat)
    {
        SetupExistingParents("en");
        EnableComposition();
        var name = $"prod-r4-composition-feature-help-en-r{repeat}";
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "CompositionFailures", name + ".json")));
        using var answer = JsonDocument.Parse(capture.RootElement.GetProperty("answer").GetString()!);
        var text = answer.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
        var tool = new DescribeFeatureTool(new FeatureExplanationService(), new AppSurfaceCatalog());
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan(name));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = text }));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: [], Locale: "en");
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Explain the requested setting.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var lines = result.Value.AiMessage!.Split('\n');
        lines.Length.Should().BeInRange(1, 3);
        lines.Should().OnlyContain(line => line.Length <= 180);
        string.Join(" ", lines).Should().Be(text);
        result.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(result.Value.AiMessage));
        result.Value.RelatedSurfaces.Should().Contain("today");
        events.Should().NotContain(item => item.Type == "delta");
        AssertStoredComposition(result.Value, client, "en");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "Open Today. Choose Edit habit. Set Frequency.", "Open Today.\nChoose Edit habit.\nSet Frequency.", true)]
    [InlineData("pt-BR", "Abra Hoje. Escolha Editar hábito. Defina a frequência.", "Abra Hoje.\nEscolha Editar hábito.\nDefina a frequência.", true)]
    [InlineData("pt-BR", "Abra Hoje. Escolha Editar hábito. Defina a frequência. Salve as alterações.", "Abra Hoje.\nEscolha Editar hábito.\nDefina a frequência.", true)]
    [InlineData("en", "Open Today. Choose Edit habit.", "Open Today. Choose Edit habit.", false)]
    [InlineData("pt-BR", "Abra Hoje. Escolha Editar hábito.", "Abra Hoje. Escolha Editar hábito.", false)]
    public async Task G13_HelpCompositionKeepsCompleteSentencesAndLegacyText(string language, string text, string expected, bool parts)
    {
        SetupExistingParents(language);
        EnableComposition();
        var tool = new DescribeFeatureTool(new FeatureExplanationService(), new AppSurfaceCatalog());
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan("prod-r4-composition-feature-help-en-r1"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = text }));
        var client = new AgentClientContext(SupportsParts: parts, PartKinds: [], Locale: language);

        var result = await handler.Handle(new(UserId, "Explain the requested setting.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(expected);
        if (parts) result.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(expected));
        else result.Value.Parts.Should().BeNull();
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task G13_HelpThatCannotFitAWholeSentenceReturnsTheExistingFailureStatus(string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var tool = new DescribeFeatureTool(new FeatureExplanationService(), new AppSurfaceCatalog());
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan("prod-r4-composition-feature-help-en-r1"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = string.Join(" ", Enumerable.Repeat("documented", 80)) + "." }));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: [], Locale: language);

        var result = await handler.Handle(new(UserId, "Explain the requested setting.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(language == "pt-BR"
            ? "Não consegui preparar uma resposta. Tente novamente." : "I couldn't prepare an answer. Please try again.");
        result.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(result.Value.AiMessage));
        AssertStoredComposition(result.Value, client, language);
    }
}
