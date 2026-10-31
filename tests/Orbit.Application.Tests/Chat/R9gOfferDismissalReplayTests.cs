using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task R9gOfferReplay_MainRoundPersistsTypedDismissalsWithoutASelector(string locale, bool readAgain)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query);
        UseProductionReadExecutor(query);
        SetupOfferSelection(query.Name, "smallStep");
        var dismissed = new HashSet<string>(StringComparer.Ordinal);
        var issued = new List<AstraOfferFact>();
        _offers.DismissedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => dismissed);
        _offers.IssuedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => issued);
        _offers.IssueAsync(UserId, Arg.Any<Guid>(), Arg.Any<AstraOfferFact>(), Arg.Any<CancellationToken>())
            .Returns(call => { issued.Add(call.Arg<AstraOfferFact>()); return Task.CompletedTask; });
        _offers.DismissAsync(UserId, Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => { dismissed.UnionWith(call.Arg<IReadOnlyList<string>>()); return Task.CompletedTask; });
        var client = OfferClient(locale);
        (await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client), default)).IsSuccess.Should().BeTrue();
        var key = issued.Single().Action.OfferKey;
        _aiIntentService.ClearReceivedCalls();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<AiToolRequest>().SystemPrompt == ProactiveOfferPlanner.SelectionInstruction
                ? Result.Success(new AiResponse { TextMessage = "{\"candidateId\":null}" })
                : Result.Success(readAgain
                    ? ToolResponse(query.Name, "read", JsonSerializer.Serialize(new
                    { relative_period = "today", orbit_read_complete = true, orbit_dismissed_offer_keys = new[] { key, "unissued" } }))
                    : new AiResponse
                    {
                        TextMessage = JsonSerializer.Serialize(new
                        { kind = "answer", aiMessage = locale == "en" ? "Okay." : "Tudo bem.", orbit_dismissed_offer_keys = new[] { key, "unissued" } }),
                        ConversationContext = TestConversationContext
                    }));

        var result = await handler.Handle(new(UserId, "Dismiss the offered action.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        dismissed.Should().ContainSingle().Which.Should().Be(key);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().OnlyContain(block => ReadBlockTelemetry.Describe(block.Block).Read!.NextAction == null);
        var requests = _aiIntentService.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "SendWithToolsAsync")
            .Select(call => (AiToolRequest)call.GetArguments()[0]!).ToArray();
        requests[0].DynamicContext.Should().Contain(key).And.Contain("previously_issued_offers");
        requests.Count(request => request.SystemPrompt == ProactiveOfferPlanner.SelectionInstruction).Should().Be(readAgain ? 1 : 0);
        _operationExecutor.ReceivedCalls().Select(call => call.GetArguments()[0]).OfType<AgentExecuteOperationRequest>()
            .Select(request => request.Arguments.TryGetProperty("orbit_dismissed_offer_keys", out _)).Should().OnlyContain(value => !value);
    }
}
