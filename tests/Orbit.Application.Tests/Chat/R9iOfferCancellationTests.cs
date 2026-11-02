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
    public async Task R9iReadOffers_SkipSelectorAndCallerCancellationStillPropagates(string locale, string mode)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        using var caller = new CancellationTokenSource();
        var selections = 0;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AiToolRequest>();
                if (request.Purpose == "offer_selection")
                {
                    selections++;
                    throw new OperationCanceledException("The obsolete selector would be unavailable.");
                }
                if (mode == "caller") caller.Cancel();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Result.Success(ToolResponse(tool.Name, "goals", JsonSerializer.Serialize(new
                {
                    include_completed = true,
                    orbit_read_complete = true,
                    orbit_offer_intent = mode == "selected" ? "inspect" : "none"
                })));
            });

        var turn = () => handler.Handle(new(UserId, "Show my goals and progress.", ClientContext: OfferClient(locale)), caller.Token);
        if (mode == "caller")
        {
            await turn.Should().ThrowAsync<OperationCanceledException>();
            selections.Should().Be(0);
            return;
        }
        var result = await turn();

        selections.Should().Be(0, "read offers use the main model declaration and never wait for a selector");
        result.IsSuccess.Should().BeTrue(result.Error);
        caller.IsCancellationRequested.Should().BeFalse();
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
