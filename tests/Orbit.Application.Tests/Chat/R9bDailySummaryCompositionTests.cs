using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void R9b_DailySummaryCompositionGuidanceKeepsFiguresAndStatusesInTheBlock()
    {
        var prompt = AiSummaryService.SystemPrompt;
        prompt.Should().Contain("Summarize the day across the person's routines in words");
        prompt.Should().Contain("End the sentence by naturally naming the server-selected next step");
        prompt.Should().Contain("Digits are allowed only inside an exact supplied stored title");
        prompt.Should().Contain("The model never selects a task");
        prompt.Should().Contain("If nextStep is null, close the day");

    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task R9b_DailySummaryCompositionDropsCapturedInvalidNarrativeWithoutRewriting(int repeat)
    {
        SetupExistingParents("pt-BR");
        EnableComposition();
        using var capture = R9bCapture($"composition-hoje-next-step-pt-BR-r{repeat}");
        var summary = capture.RootElement.GetProperty("summaryResult").Deserialize<DailySummaryResponse>(JsonSerializerOptions.Web)!;
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(summary));
        var tool = new GetDailySummaryTool(_mediator, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(new() { ToolCalls = BrainDumpCalls(capture), ConversationContext = new() });
        var client = new Orbit.Domain.Models.AgentClientContext(SupportsParts: true, SupportsDaySummaryCard: true,
            PartKinds: ["daySummary"], Locale: "pt-BR");
        var events = new List<ChatStreamEvent>();

        var response = await handler.Handle(new(UserId, "Continue.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        response.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("daySummary");
        response.Value.AiMessage.Should().BeNullOrEmpty();
        events.Where(item => item.Type == "delta").Should().BeEmpty();
        await AssertNoCompositionContinuation();
        AssertStoredComposition(response.Value, client, "pt-BR");
    }

    private static JsonDocument R9bCapture(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "CompositionFailures", "prod-r9b-" + name + ".json")));
}
