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
    public void R9b_DailySummaryCompositionGuidanceKeepsFiguresAndStatusesInTheBlock(string language)
    {
        var prompt = AiSummaryService.BuildSummaryPrompt([], new(Today, Today, Today, language,
            new TimeOnly(11, 0), 1, 0, new Dictionary<Guid, DateOnly>()));

        prompt.Should().Contain("Never include figures, times, dates, counts, durations, rates or quantities, even when supplied; the daySummary block already shows them.");
        prompt.Should().Contain("Never restate a completed, skipped or pending status from the block; name only the next actionable item with a short reason based on its order.");
        prompt.Should().Contain("Never invent habit or goal names; use only supplied facts");
        prompt.Should().Contain("An overdue or time-bound item in another group may take priority only when clearly more urgent; explain the priority in one short clause based on its order, without figures or times");
        prompt.Should().Contain("Use supplied timing and progress only to choose the next item, never in the sentence. This applies equally to English and Brazilian Portuguese.");
        prompt.Should().NotContain("using supplied timing or order");
        prompt.Should().NotContain("Mention a completion only as the reason");
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
