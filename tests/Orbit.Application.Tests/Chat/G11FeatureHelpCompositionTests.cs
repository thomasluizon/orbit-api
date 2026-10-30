using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G11_CapturedFeatureHelpKeepsWholeNarrativeLinesAndLimitsTheirCount(bool longLine)
    {
        SetupExistingParents("en");
        EnableComposition();
        var features = new FeatureExplanationService();
        var catalog = new AgentCatalogService();
        _catalogService.GetSurfaces().Returns(catalog.GetSurfaces());
        var steps = catalog.GetSurfaces().Single(surface => surface.Id == "today").HowToSteps;
        var paragraph = features.Get("frequencies")!.Body.Split('\n').First(line => line.StartsWith("A habit's frequency", StringComparison.Ordinal));
        paragraph.Length.Should().BeGreaterThan(180);
        var first = longLine ? paragraph : steps[0];
        var answer = string.Join("\n", first, steps[0], steps[1], steps[2]);
        var tool = new DescribeFeatureTool(features, new AppSurfaceCatalog());
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan("prod-r1-composition-feature-help-en-r3"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = answer }));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: [], Locale: "en");
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Explain the requested setting.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var expected = string.Join("\n", first, steps[0], steps[1]);
        result.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(expected));
        result.Value.AiMessage.Should().Be(expected);
        AssertStoredComposition(result.Value, client, "en");
        events.Should().NotContain(item => item.Type == "delta");
    }
}
