using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("pt-BR", "Deixe eu escolher quais dos meus hábitos concluir, sem agir ainda.", "log_habits")]
    [InlineData("en", "Let me pick which of my habits to complete, without acting yet.", "log_habits")]
    [InlineData("en", "Let me choose which habits to skip today.", "skip_habits")]
    public async Task SelectionPickList_ForSelectionReadIssuesThePickListWithoutASecondModelRound(string language, string message, string action)
    {
        var habits = SetupPickListTurn(language);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [ModelPickList(habits, action)] }));

        var result = await PickListTurnAsync(message, language);

        result.IsSuccess.Should().BeTrue(result.Error);
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraPickListBlock>().Subject;
        block.Action.Should().Be(action);
        block.Items.Select(item => item.Id).Should().BeEquivalentTo(habits.Select(habit => habit.Id.ToString()));
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        _mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task SelectionPickList_UnclearActionStillUsesTheModelRound()
    {
        var habits = SetupPickListTurn("en");
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [ModelPickList(habits, "log_habits")] }));

        var result = await PickListTurnAsync("Let me pick some of my habits.", "en");

        result.IsSuccess.Should().BeTrue(result.Error);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    private Habit[] SetupPickListTurn(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        var habits = new[] { CreateHabit(language == "en" ? "Water" : "Beber água"), CreateHabit(language == "en" ? "Walk" : "Caminhada") };
        SetupSelectionHabits(habits);
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [AskSelectionRead()] });
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        return habits;
    }

    private async Task<Result<ChatResponse>> PickListTurnAsync(string message, string language)
    {
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        return await handler.Handle(new(UserId, message, ClientContext: new(SupportsParts: true, PartKinds: ["pickList"], Locale: language)), default);
    }

    private AiToolCall ModelPickList(IEnumerable<Habit> habits, string action) => new("ask", "model-pick", JsonSerializer.SerializeToElement(new
    {
        input = new
        {
            form = "pickList",
            missing_input = "bulk_selection",
            title = "Choose habits",
            action,
            items = habits.Select(habit => new
            {
                id = habit.Id.ToString(),
                label = habit.Title,
                arguments = new { habit_id = habit.Id.ToString(), date = Today.ToString("yyyy-MM-dd") }
            })
        }
    }));
}
