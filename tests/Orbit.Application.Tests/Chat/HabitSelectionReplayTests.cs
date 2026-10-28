using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task SelectionReplay_DuplicateLookupBlocksCapturedGuessedWrite(bool composition, bool declaredRead, bool sameRound)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var habits = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var write = ReadTool("log_habit", new ToolResult(true));
        write.IsReadOnly.Returns(false);
        var handler = CreateHandler(query, write);
        UseProductionReadExecutor(query, write);
        var captured = GateSevenReplay("ambiguity-pt-r3");
        if (declaredRead)
        {
            var readArguments = JsonNode.Parse(captured.ToolCalls![0].Args.GetRawText())!.AsObject();
            readArguments["for_selection"] = false;
            captured = captured with { ToolCalls = [captured.ToolCalls[0] with { Args = JsonSerializer.SerializeToElement(readArguments) }, captured.ToolCalls[1]] };
        }
        SetupAiResponse(captured with { ToolCalls = [captured.ToolCalls![0]] });
        var guessed = captured.ToolCalls![1];
        var arguments = JsonNode.Parse(guessed.Args.GetRawText())!.AsObject();
        arguments["habit_id"] = habits[0].Id.ToString();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(captured with { ToolCalls = [guessed with { Args = JsonSerializer.SerializeToElement(arguments) }] }),
                Result.Success(new AiResponse { TextMessage = "[[orbit:habits:today]]" }));
        if (sameRound)
        {
            SetupAiResponse(captured with { ToolCalls = [captured.ToolCalls[0], guessed with { Args = JsonSerializer.SerializeToElement(arguments) }] });
            ContinueWithReadDirective("[[orbit:habits:today]]");
        }

        var result = await handler.Handle(new(UserId, "Marca a caminhada como feita.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(call =>
            call.OperationId == "log_habit"), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().EndWith("?");
    }

    [Theory]
    [InlineData(true, "en")]
    [InlineData(false, "pt-BR")]
    public async Task SelectionReplay_DuplicateTerminalReadCannotCompleteSelection(bool parts, string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        SetupSelectionHabits(CreateHabit("Caminhada"), CreateHabit("Caminhada"));
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query);
        UseProductionReadExecutor(query);
        SetupAiResponse(GateSevenReplay("ambiguity-pt-r1"));
        ContinueWithReadDirective("[[orbit:habits:today]]");

        var result = await handler.Handle(new(UserId, "Marca a caminhada como feita.",
            ClientContext: new(SupportsParts: parts, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(language == "en"
            ? "Which habit would you like to use for “Caminhada”?"
            : "Qual hábito você quer usar para “Caminhada”?");
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.HabitList.Should().BeNull();
        result.Value.Operations!.Single().Payload.Should().BeOfType<HabitListCard>().Which.Selection!
            .Candidates.Should().HaveCount(2);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task SelectionReplay_DeclaredListKeepsMatchingRecordsAndCompletesWithoutAsk()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query);
        UseProductionReadExecutor(query);
        var captured = GateSevenReplay("ambiguity-pt-r1");
        var call = captured.ToolCalls!.Single();
        var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        args["for_selection"] = false;
        SetupAiResponse(captured with { ToolCalls = [call with { Args = JsonSerializer.SerializeToElement(args) }] });

        var result = await handler.Handle(new(UserId, "Mostre meus hábitos de caminhada.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<HabitListCard>().Which.Items.Select(item => item.Id)
            .Should().Equal(habits.Select(habit => habit.Id.ToString()));
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task SelectionReplay_ExplicitRecordCanReachWriteExecutor()
    {
        SetupUserAndPayGate();
        var habit = CreateHabit("Caminhada");
        SetupSelectionHabits(habit, CreateHabit("Caminhada"));
        var write = ReadTool("log_habit", new ToolResult(true));
        write.IsReadOnly.Returns(false);
        var handler = CreateHandler(write);
        UseProductionReadExecutor(write);
        var call = GateSevenReplay("ambiguity-pt-r3").ToolCalls![1];
        var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        args["habit_id"] = habit.Id.ToString();
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [call with { Args = JsonSerializer.SerializeToElement(args) }]
        });
        ContinueWithReadDirective("Done.");

        var result = await handler.Handle(new(UserId, "Use this habit: " + habit.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == "log_habit" && request.Arguments.GetProperty("habit_id").GetString() == habit.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    public async Task SelectionReplay_CapturedTerminalLookupContinuesToPickList(int repeat, bool declaredSelection, bool singleCandidate)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = singleCandidate ? [CreateHabit("Beber água")] : new[] { CreateHabit("Beber água"), CreateHabit("Caminhada") };
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, RealAskTool());
        UseProductionReadExecutor(query);
        var captured = GateSevenReplay("composition-pickList-pt-BR-r" + repeat);
        if (declaredSelection)
        {
            var readCall = captured.ToolCalls!.Single();
            var readArgs = JsonNode.Parse(readCall.Args.GetRawText())!.AsObject();
            readArgs["for_selection"] = true;
            captured = captured with { ToolCalls = [readCall with { Args = JsonSerializer.SerializeToElement(readArgs) }] };
        }
        SetupAiResponse(captured);
        var ask = HonestyReplay("composition-pickList-pt-BR-r1");
        var call = ask.ToolCalls!.Single();
        var arguments = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        var items = arguments["items"]!.AsArray();
        if (singleCandidate) items.RemoveAt(1);
        for (var index = 0; index < habits.Length; index++)
        {
            items[index]!["id"] = habits[index].Id.ToString();
            items[index]!["arguments"]!["habit_id"] = habits[index].Id.ToString();
        }
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ask with { ToolCalls = [call with { Args = JsonSerializer.SerializeToElement(arguments) }] }));

        var result = await handler.Handle(new(UserId, "Deixe eu escolher quais dos meus hábitos concluir, sem agir ainda.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "pickList"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraPickListBlock>().Subject;
        block.Items.Select(item => item.Id).Should().Equal(habits.Select(habit => habit.Id.ToString()));
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    private void SetupSelectionHabits(params Habit[] habits) => _habitRepo.FindAsync(
        Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
        Arg.Any<CancellationToken>()).Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToArray());

    private static AiResponse GateSevenReplay(string name)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateSeven", name + ".json")));
        return new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = capture.RootElement.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
                new AiToolCall(call.GetProperty("name").GetString()!, "gate-seven-" + index,
                    call.GetProperty("arguments").Clone())).ToArray()
        };
    }
}
