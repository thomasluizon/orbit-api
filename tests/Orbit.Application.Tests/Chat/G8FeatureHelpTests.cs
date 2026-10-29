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

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "This explanation does not cover editing a habit's time.")]
    [InlineData("pt-BR", "Esta explicação não informa como editar o horário de um hábito.")]
    public async Task G8_CapturedFeatureHelpSuppliesDocumentedAnswerScopeToNarrative(string language, string answer)
    {
        SetupExistingParents(language);
        EnableComposition();
        var features = new FeatureExplanationService();
        var tool = new DescribeFeatureTool(features);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan("g8-composition-feature-help-pt-BR-r1"));
        JsonElement payload = default;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var result = call.Arg<IReadOnlyList<AiToolCallResult>>().Should().ContainSingle().Subject;
                result.Name.Should().Be(tool.Name);
                result.Success.Should().BeTrue();
                payload = JsonSerializer.SerializeToElement(result.Payload);
                return Result.Success(new AiResponse { TextMessage = answer });
            });
        var events = new List<ChatStreamEvent>();

        var response = await handler.Handle(new(UserId, "Explain the requested setting.", ClientContext: new(
            SupportsParts: true, PartKinds: [], Locale: language),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        payload.GetProperty("markdown").GetString().Should().Be(features.Get("schedule-math")!.Body);
        payload.GetProperty("related_surfaces").EnumerateArray().Select(item => item.GetString()).Should().Equal("today");
        payload.TryGetProperty("answer_scope", out var scope).Should().BeTrue("feature help must distinguish documented rules from unsupported editing instructions");
        scope.GetString().Should().Contain("explicitly documented in markdown").And.Contain("not editing instructions")
            .And.Contain("does not cover it").And.Contain("person's language");
        var declaration = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>())
            .SelectMany(request => request.ToolDeclarations).Select(value => JsonSerializer.SerializeToElement(value))
            .Single(value => value.GetProperty("name").GetString() == tool.Name);
        declaration.GetProperty("description").GetString().Should().Contain(scope.GetString()!);
        response.Value.Parts.Should().ContainSingle().Which.Should().Be(new ChatTextPart(answer));
        response.Value.AiMessage.Should().Be(answer);
        response.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.Value.PendingOperations.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        AssertStoredComposition(response.Value, new(SupportsParts: true, PartKinds: [], Locale: language), language);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
