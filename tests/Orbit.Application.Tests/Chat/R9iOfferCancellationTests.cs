using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "timeout")]
    [InlineData("pt-BR", "timeout")]
    [InlineData("en", "failure")]
    [InlineData("pt-BR", "failure")]
    [InlineData("en", "selected")]
    [InlineData("pt-BR", "selected")]
    [InlineData("en", "caller")]
    public async Task R9iOfferSelection_DeadlinePreservesTheReadAndCallerCancellationStillPropagates(string locale, string mode)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        using var caller = new CancellationTokenSource();
        CancellationToken selectorToken = default;
        var selections = 0;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<AiToolRequest>();
                if (request.Purpose != "offer_selection")
                    return Result.Success(ToolResponse(tool.Name, "goals", """{"include_completed":true,"orbit_read_complete":true}"""));
                selections++;
                selectorToken = call.Arg<CancellationToken>();
                selectorToken.CanBeCanceled.Should().BeTrue();
                selectorToken.IsCancellationRequested.Should().BeFalse();
                caller.IsCancellationRequested.Should().BeFalse();
                request.ToolDeclarations.Should().BeEmpty();
                if (mode is "timeout" or "caller")
                {
                    if (mode == "caller") caller.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, selectorToken);
                }
                if (mode == "failure") return Result.Failure<AiResponse>(ErrorMessages.AiUnavailable);
                using var facts = JsonDocument.Parse(request.DynamicContext!);
                var candidate = facts.RootElement.GetProperty("Candidates").EnumerateArray()
                    .Single(item => item.GetProperty("Family").GetString() == "inspect");
                return Result.Success(new AiResponse
                {
                    TextMessage = JsonSerializer.Serialize(new { candidateId = candidate.GetProperty("Id").GetString() })
                });
            });

        var turn = () => handler.Handle(new(UserId, "Show my goals and progress.", ClientContext: OfferClient(locale)), caller.Token);
        if (mode == "caller")
        {
            await turn.Should().ThrowAsync<OperationCanceledException>();
            selections.Should().Be(1);
            return;
        }
        var result = await turn();

        selections.Should().Be(1, "the test must reach the optional selector rather than a concrete offer shortcut");
        result.IsSuccess.Should().BeTrue(result.Error);
        caller.IsCancellationRequested.Should().BeFalse();
        selectorToken.IsCancellationRequested.Should().Be(mode == "timeout");
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which;
        block.Kind.Should().Be("goalList");
        var card = block.Block.Should().BeOfType<GoalListCard>().Subject;
        card.Items.Should().BeEmpty();
        if (mode == "selected") card.Read!.NextAction.Should().NotBeNull();
        else
        {
            card.Read!.NextAction.Should().BeNull();
            await _offers.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default!, default);
        }
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await AssertNoCompositionContinuation();
    }
}
