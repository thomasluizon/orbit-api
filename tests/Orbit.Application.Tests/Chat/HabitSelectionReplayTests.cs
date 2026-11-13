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
            .Should().BeEquivalentTo(habits.Select(habit => habit.Id.ToString()));
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionReplay_ExplicitRecordCanReachWriteExecutor(bool withUnrelatedRead)
    {
        SetupUserAndPayGate();
        var habit = CreateHabit("Water");
        SetupSelectionHabits(habit, CreateHabit("Caminhada"), CreateHabit("Caminhada"));
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var write = ReadTool("log_habit", new ToolResult(true));
        write.IsReadOnly.Returns(false);
        var handler = CreateHandler(query, write);
        UseProductionReadExecutor(query, write);
        var call = GateSevenReplay("ambiguity-pt-r3").ToolCalls![1];
        var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        args["habit_id"] = habit.Id.ToString();
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [call with { Args = JsonSerializer.SerializeToElement(args) }]
        });
        ContinueWithReadDirective("Done.");
        if (withUnrelatedRead)
        {
            var read = GateSevenReplay("ambiguity-pt-r3").ToolCalls![0];
            var readArgs = JsonNode.Parse(read.Args.GetRawText())!.AsObject();
            readArgs["for_selection"] = false;
            SetupAiResponse(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls = [read with { Args = JsonSerializer.SerializeToElement(readArgs) }, call with { Args = JsonSerializer.SerializeToElement(args) }]
            });
        }

        var result = await handler.Handle(new(UserId, "Use this habit: " + habit.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == "log_habit" && request.Arguments.GetProperty("habit_id").GetString() == habit.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SelectionReplay_CoalescedTagsKeepOnlyUnrelatedTargets(bool sameRound, bool candidateFirst)
    {
        SetupUserAndPayGate();
        var water = CreateHabit("Water");
        var candidates = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada") };
        SetupSelectionHabits(water, candidates[0], candidates[1]);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var tags = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(query, tags);
        var held = HoldSelectionReplayWrites(query, tags);
        var writes = new[]
        {
            new AiToolCall(tags.Name, "water", JsonSerializer.SerializeToElement(new { habit_id = "Water", tag_names = new[] { "Health" } })),
            new AiToolCall(tags.Name, "candidate", JsonSerializer.SerializeToElement(new { habit_id = candidates[0].Id, tag_names = new[] { "Health" } }))
        };
        ReplaySelectionWrites(sameRound, candidateFirst ? writes.Reverse().ToArray() : writes);

        var result = await handler.Handle(new(UserId, "Apply the requested changes.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle().Which.OperationId.Should().Be(tags.Name);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(water.Id);
        result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Should()
            .NotContain(item => candidates.Any(candidate => candidate.Id == item.EntityId));
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(outcome => outcome.Id == "candidate" && !outcome.Success)),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionReplay_BulkFilterBlocksCandidatesAndKeepsUnrelatedWrite(bool sameRound)
    {
        SetupUserAndPayGate();
        var water = CreateHabit("Water");
        var candidates = new[] { CreateHabit("Caminhada"), CreateHabit("Caminhada") };
        SetupSelectionHabits(water, candidates[0], candidates[1]);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(query, update);
        var held = HoldSelectionReplayWrites(query, update);
        ReplaySelectionWrites(sameRound,
            new AiToolCall(update.Name, "candidate", ParseArguments("""{"filter":{"search":"Caminhada"},"updates":{"description":"Updated"}}""")),
            new AiToolCall(update.Name, "water", ParseArguments("""{"filter":{"search":"Water"},"updates":{"description":"Updated"}}""")));

        var result = await handler.Handle(new(UserId, "Apply the requested changes.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle().Which.Arguments.GetProperty("filter").GetProperty("search").GetString().Should().Be("Water");
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(water.Id);
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == update.Name && request.Arguments.GetProperty("filter").GetProperty("search").GetString() == "Caminhada"),
            Arg.Any<CancellationToken>());
    }

    private List<AgentExecuteOperationRequest> HoldSelectionReplayWrites(QueryHabitsTool query, params IAiTool[] writes)
    {
        var blocks = UseProductionReadExecutor(query);
        var held = HoldReplayWrites(writes);
        var executor = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(query, blocks);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == query.Name),
                Arg.Any<CancellationToken>())
            .Returns(call => executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        return held;
    }

    [Fact]
    public async Task SelectionReplay_MultipleLookupsKeepEveryUnansweredCandidateSet()
    {
        SetupUserAndPayGate();
        var water = CreateHabit("Water");
        var candidate = CreateHabit("Caminhada");
        SetupSelectionHabits(water, candidate, CreateHabit("Caminhada"));
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var tags = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(query, tags);
        var held = HoldSelectionReplayWrites(query, tags);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new(query.Name, "first-selection", ParseArguments("""{"search":"Water","for_selection":true}""")),
                new(query.Name, "second-selection", ParseArguments("""{"for_selection":true}""")),
                new(tags.Name, "candidate", JsonSerializer.SerializeToElement(new { habit_id = candidate.Id, tag_names = new[] { "Health" } }))
            ]
        });
        ReplyToReplay("Which habit would you like to choose?");

        var result = await handler.Handle(new(UserId, "Apply the requested changes.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().BeEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == tags.Name), Arg.Any<CancellationToken>());
    }

    private void ReplaySelectionWrites(bool sameRound, params AiToolCall[] writes)
    {
        var read = new AiToolCall("query_habits", "lookup",
            ParseArguments("""{"search":"Caminhada","for_selection":false,"orbit_read_complete":false}"""));
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = sameRound ? [read, .. writes] : [read]
        });
        var final = Result.Success(new AiResponse { TextMessage = "Review the changes." });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(sameRound ? final : Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = writes }), final);
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
            .Returns(Result.Success(ask with { ToolCalls = [AskWithMissingInput(call with { Args = JsonSerializer.SerializeToElement(arguments) }, "bulk_selection")] }));

        var result = await handler.Handle(new(UserId, "Deixe eu escolher quais dos meus hábitos concluir, sem agir ainda.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "pickList"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AiPickListBlock>().Subject;
        if (declaredSelection)
            block.Items.Select(item => item.Id).Should().BeEquivalentTo(habits.Select(habit => habit.Id.ToString()));
        else
            block.Items.Select(item => item.Id).Should().Equal(habits.Select(habit => habit.Id.ToString()));
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _aiIntentService.Received(declaredSelection ? 0 : 1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    private (Habit Parent, Habit[] Children) SetupSameTitleChildren(string title, int count)
    {
        var parent = CreateHabit("Water");
        var children = Enumerable.Range(0, count).Select(_ => CreateHabit(title)).ToArray();
        foreach (var child in children) child.SetParentHabitId(parent.Id);
        SetupSelectionHabits([parent, .. children]);
        return (parent, children);
    }

    [Theory]
    [InlineData(true, "en")]
    [InlineData(false, "en")]
    [InlineData(true, "pt-BR")]
    [InlineData(false, "pt-BR")]
    public async Task SelectionReplay_SameTitleChildrenSplitIntoDeleteAndRenameReachOnePreview(bool forSelection, string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var (_, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(query, delete, update);
        var held = HoldSelectionReplayWrites(query, delete, update);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(query.Name, "lookup", JsonSerializer.SerializeToElement(new
            {
                search = "Water 710ml", for_selection = forSelection, orbit_read_complete = false
            }))]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls =
                [
                    new(delete.Name, "delete", JsonSerializer.SerializeToElement(new { habit_ids = children[4..].Select(child => child.Id) })),
                    new(update.Name, "rename", JsonSerializer.SerializeToElement(new
                    {
                        filter = new { habit_ids = children[..4].Select(child => child.Id) },
                        updates = new { title = "Water - 1.18L" }
                    }))
                ]
            }), Result.Success(new AiResponse { TextMessage = "Review the changes." }));

        var result = await handler.Handle(new(UserId,
            "eu tenho 7 habitos chamados Water - 710ml deleta dois desses, e os outros 4 coloca Water - 1.18L",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Select(request => request.OperationId).Should().BeEquivalentTo([delete.Name, update.Name],
            "the reply was: " + result.Value.AiMessage);
        result.Value.PendingOperations.Should().HaveCount(2);
        result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo(children.Select(child => child.Id));
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().NotContain("Which habit").And.NotContain("Qual hábito");
    }

    private async Task<(Result<ChatResponse> Result, List<AgentExecuteOperationRequest> Held, Habit[] Children)>
        RunSameTitleTurnAsync(string message, string search, Func<Habit[], AiToolCall[]> writes, string language = "en",
            string title = "Water - 710ml", int count = 6)
    {
        var parent = CreateHabit("Water");
        var children = Enumerable.Range(0, count).Select(_ => CreateHabit(title)).ToArray();
        foreach (var child in children) child.SetParentHabitId(parent.Id);
        var (result, held) = await RunSelectionTurnAsync(message, search, language, [parent, .. children], writes(children));
        return (result, held, children);
    }

    /// <summary>
    /// A turn that reads the habits with a declared list search and then sends each round of writes in its own
    /// model response.
    /// </summary>
    private async Task<(Result<ChatResponse> Result, List<AgentExecuteOperationRequest> Held)> RunSelectionTurnAsync(
        string message, string search, string language, Habit[] habits, params AiToolCall[][] rounds)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var deleteOne = new DeleteHabitTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var updateOne = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var handler = CreateHandler(query, delete, deleteOne, update, updateOne);
        var held = HoldSelectionReplayWrites(query, delete, deleteOne, update, updateOne);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(query.Name, "lookup", JsonSerializer.SerializeToElement(new
            {
                search, for_selection = false, orbit_read_complete = false
            }))]
        });
        var replies = rounds.Select(round => Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = round }))
            .Append(Result.Success(new AiResponse { TextMessage = "Review the changes." })).ToArray();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(replies[0], replies[1..]);
        var result = await handler.Handle(new ProcessUserChatCommand(UserId, message,
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);
        return (result, held);
    }

    private static AiToolCall DeleteCall(IEnumerable<Habit> habits) => new("bulk_delete_habits", "delete",
        JsonSerializer.SerializeToElement(new { habit_ids = habits.Select(habit => habit.Id) }));

    private static AiToolCall RenameCall(IEnumerable<Habit> habits) => new("bulk_update_habits", "rename",
        JsonSerializer.SerializeToElement(new
        {
            filter = new { habit_ids = habits.Select(habit => habit.Id) },
            updates = new { title = "Water - 1.18L" }
        }));

    [Fact]
    public async Task SelectionReplay_SearchThatMatchesParentAndChildrenStillSettlesTheSplit()
    {
        var run = await RunSameTitleTurnAsync("eu tenho 7 habitos chamados Water deleta dois desses, e os outros 4 coloca Water - 1.18L",
            "Water", children => [DeleteCall(children[4..]), RenameCall(children[..4])], "pt-BR");

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"],
            "the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.PendingOperations.Should().HaveCount(2);
        run.Result.Value.AiMessage.Should().NotContain("Qual hábito");
    }

    [Theory]
    [InlineData("delete the last 2 Water - 710ml habits", true)]
    [InlineData("apague os 2 últimos hábitos Water - 710ml", true)]
    [InlineData("delete two of the Water - 710ml habits", false)]
    public async Task SelectionReplay_OneActionOnAnOrderedSubsetRunsOnlyWhenTheMessageNamesTheOrder(string message, bool allowed)
    {
        var run = await RunSameTitleTurnAsync(message, "Water 710ml", children => [DeleteCall(children[4..])]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().Equal(allowed ? ["bulk_delete_habits"] : []);
        if (!allowed)
            run.Result.Value.AiMessage.Should().Be("Which habit would you like to use for “Water - 710ml”?");
    }

    [Theory]
    [InlineData("rename all the Water - 710ml habits to Water - 1.18L", true)]
    [InlineData("renomeie todos os hábitos chamados Water - 710ml para Water - 1.18L", true)]
    [InlineData("rename the Water - 710ml habit to Water - 1.18L", false)]
    public async Task SelectionReplay_WholeSetActionRunsOnlyWhenTheMessageNamesTheSet(string message, bool allowed)
    {
        var run = await RunSameTitleTurnAsync(message, "Water 710ml", children => [RenameCall(children)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().Equal(allowed ? ["bulk_update_habits"] : []);
        run.Result.Value.PendingOperations.Should().HaveCount(allowed ? 1 : 0);
    }

    [Fact]
    public async Task SelectionReplay_TypedFullRepeatWithNoCardReachesThePreview()
    {
        var run = await RunSameTitleTurnAsync(
            "ja respondi isos, eu falei pra manter os 4 primeiros, isso significa q é pra excluir os 2 ultimos",
            "Water 710ml", children => [DeleteCall(children[4..]), RenameCall(children[..4])], "pt-BR");

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"]);
        run.Result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
    }

    [Fact]
    public async Task SelectionReplay_SingularAmbiguousRequestStillAsksOnceAndQuotesTheStoredTitle()
    {
        var run = await RunSameTitleTurnAsync("troque o título do hábito Water 710ml para Water - 1.18L", "Water 710ml",
            children => [RenameCall(children[..1])], "pt-BR");

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().BeEmpty();
        run.Result.Value.AiMessage.Should().Be("Qual hábito você quer usar para “Water - 710ml”?");
    }

    private static AiToolCall UpdateCall(string id, IEnumerable<Habit> habits, object updates) => new("bulk_update_habits", id,
        JsonSerializer.SerializeToElement(new { filter = new { habit_ids = habits.Select(habit => habit.Id) }, updates }));

    private static readonly object DailyUpdate = new { frequency_unit = "Day", frequency_quantity = 1 };

    private static string SelectionQuestionFor(string title, string language) => language == "en"
        ? $"Which habit would you like to use for “{title}”?"
        : $"Qual hábito você quer usar para “{title}”?";

    [Theory]
    [InlineData("Change Walk to every day", "Walk", "en", false)]
    [InlineData("Move Walk to each morning", "Walk", "en", false)]
    [InlineData("Set Walk to every other day", "Walk", "en", false)]
    [InlineData("All right, set Walk to daily", "Walk", "en", false)]
    [InlineData("Set Walk to daily, that is all", "Walk", "en", false)]
    [InlineData("Set Both Sides Plank to daily", "Both Sides Plank", "en", false)]
    [InlineData("Change both Walk habits to every day", "Walk", "en", true)]
    [InlineData("Mude Caminhada para todos os dias", "Caminhada", "pt-BR", false)]
    [InlineData("Mude Caminhada para todas as manhãs", "Caminhada", "pt-BR", false)]
    [InlineData("Tudo bem, mude Caminhada para diária", "Caminhada", "pt-BR", false)]
    [InlineData("Mude Caminhada para diária, é tudo", "Caminhada", "pt-BR", false)]
    [InlineData("Mude Prancha ambos os lados para diária", "Prancha ambos os lados", "pt-BR", false)]
    [InlineData("Mude todos os hábitos Caminhada para todos os dias", "Caminhada", "pt-BR", true)]
    public async Task SelectionReplay_WholeSetWordCountsOnlyWhenItScopesTheHabits(string message, string title, string language, bool allowed)
    {
        var run = await RunSameTitleTurnAsync(message, title, children => [UpdateCall("schedule", children, DailyUpdate)],
            language, title, 2);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().Equal(allowed ? ["bulk_update_habits"] : [],
            "the reply was: " + run.Result.Value.AiMessage);
        if (!allowed)
            run.Result.Value.AiMessage.Should().Be(SelectionQuestionFor(title, language));
    }

    [Theory]
    [InlineData("One last thing: rename Walk to Run", "Walk", "en", false)]
    [InlineData("I walked last week 3 times, rename Walk to Run", "Walk", "en", false)]
    [InlineData("Rename Walk to Run, I did it 2 times last week", "Walk", "en", false)]
    [InlineData("Rename Top 3 Stretch to Stretch", "Top 3 Stretch", "en", false)]
    [InlineData("Rename the first one to Run, I walked last week", "Walk", "en", true)]
    [InlineData("Uma última coisa: renomeie Caminhada para Corrida", "Caminhada", "pt-BR", false)]
    [InlineData("Nos últimos dias caminhei pouco, renomeie Caminhada para Corrida", "Caminhada", "pt-BR", false)]
    [InlineData("Nas últimas 2 semanas caminhei pouco, renomeie Caminhada para Corrida", "Caminhada", "pt-BR", false)]
    [InlineData("Renomeie Ler as 10 primeiras páginas para Ler", "Ler as 10 primeiras páginas", "pt-BR", false)]
    [InlineData("Renomeie a primeira das duas para Corrida, caminhei na última semana", "Caminhada", "pt-BR", true)]
    public async Task SelectionReplay_OrderWordCountsOnlyWhenItScopesTheHabits(string message, string title, string language, bool allowed)
    {
        var run = await RunSameTitleTurnAsync(message, title,
            children => [UpdateCall("rename", children[..1], new { title = "Renamed" })], language, title, 2);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().Equal(allowed ? ["bulk_update_habits"] : [],
            "the reply was: " + run.Result.Value.AiMessage);
        if (!allowed)
            run.Result.Value.AiMessage.Should().Be(SelectionQuestionFor(title, language));
    }

    [Theory]
    [InlineData("Set Walk to daily", "Walk", "en")]
    [InlineData("Change Walk to every day", "Walk", "en")]
    [InlineData("Mude Caminhada para diária", "Caminhada", "pt-BR")]
    [InlineData("Mude Caminhada para todos os dias", "Caminhada", "pt-BR")]
    public async Task SelectionReplay_OneChangeSentOncePerDuplicateIsNotASplit(string message, string title, string language)
    {
        var run = await RunSameTitleTurnAsync(message, title,
            children => [UpdateCall("first", children[..1], DailyUpdate), UpdateCall("second", children[1..], DailyUpdate)],
            language, title, 2);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().BeEmpty("the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.AiMessage.Should().Be(SelectionQuestionFor(title, language));
    }

    /// <summary>
    /// delete_habit for one Walk and bulk_delete_habits for the other make one delete in two forms, and update_habit
    /// and bulk_update_habits to one title make one rename in two forms: neither is a split.
    /// </summary>
    [Theory]
    [InlineData("Delete Walk", "Walk", "en", null)]
    [InlineData("Exclua Caminhada", "Caminhada", "pt-BR", null)]
    [InlineData("Rename Walk to Morning walk", "Walk", "en", "Morning walk")]
    [InlineData("Renomeie Caminhada para Caminhada cedo", "Caminhada", "pt-BR", "Caminhada cedo")]
    public async Task SelectionReplay_SingleAndBulkFormOfOneChangeAreNotASplit(string message, string title, string language,
        string? rename)
    {
        var run = await RunSameTitleTurnAsync(message, title, children => rename is null
            ?
            [
                new("delete_habit", "first", JsonSerializer.SerializeToElement(new { habit_id = children[0].Id })),
                new("bulk_delete_habits", "second", JsonSerializer.SerializeToElement(new { habit_ids = children[1..].Select(child => child.Id) }))
            ]
            : [RenameOne("first", children[0], rename), UpdateCall("second", children[1..], new { title = rename, description = (string?)null })],
            language, title, 2);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().BeEmpty("the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.AiMessage.Should().Be(SelectionQuestionFor(title, language));
    }

    /// <summary>
    /// update_habit to one title for one Walk and bulk_update_habits to another title for the other make two
    /// changes in two forms: a split, so both writes reach the preview and the person gets no question.
    /// </summary>
    [Theory]
    [InlineData("Rename one Walk to Morning walk and the other to Evening walk", "Walk", "en", "Morning walk", "Evening walk")]
    [InlineData("Renomeie uma Caminhada para Caminhada cedo e a outra para Caminhada tarde", "Caminhada", "pt-BR",
        "Caminhada cedo", "Caminhada tarde")]
    public async Task SelectionReplay_SingleAndBulkFormWithDifferentChangesSplitTheGroup(string message, string title,
        string language, string first, string second)
    {
        var run = await RunSameTitleTurnAsync(message, title,
            children => [RenameOne("first", children[0], first), UpdateCall("second", children[1..], new { title = second, description = (string?)null })],
            language, title, 2);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().BeEquivalentTo(["update_habit", "bulk_update_habits"],
            "the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.AiMessage.Should().NotBe(SelectionQuestionFor(title, language));
    }

    /// <summary>
    /// An update_habit rename as the strict tool schema makes the model send it: each optional field that the rename
    /// leaves unchanged is null.
    /// </summary>
    private static AiToolCall RenameOne(string id, Habit habit, string title) => new("update_habit", id,
        JsonSerializer.SerializeToElement(new
        {
            habit_id = habit.Id,
            title,
            description = (string?)null,
            goal_ids = (string[]?)null,
            parent_habit_id = (string?)null
        }));

    [Theory]
    [InlineData("Rename one Walk to Morning walk and the other to Evening walk", "Walk", "en")]
    [InlineData("Renomeie uma Caminhada para Caminhada cedo e a outra para Caminhada tarde", "Caminhada", "pt-BR")]
    public async Task SelectionReplay_DifferentChangesFromOneToolStillSplitTheGroup(string message, string title, string language)
    {
        var run = await RunSameTitleTurnAsync(message, title,
            children => [UpdateCall("first", children[..1], new { title = "Morning walk" }),
                UpdateCall("second", children[1..], new { title = "Evening walk" })], language, title, 2);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Select(request => request.OperationId).Should().Equal(["bulk_update_habits", "bulk_update_habits"],
            "the reply was: " + run.Result.Value.AiMessage);
    }

    [Theory]
    [InlineData("Rename Walk to Run", "en")]
    [InlineData("Rename Walk to Run, thanks for all the help", "en")]
    [InlineData("Rename Walk to Run, we both like it", "en")]
    [InlineData("Rename the first one to Run", "en")]
    [InlineData("Renomeie Caminhada para Corrida", "pt-BR")]
    [InlineData("Renomeie Caminhada para Corrida, obrigado por tudo", "pt-BR")]
    [InlineData("Renomeie Caminhada para Corrida, todos gostamos", "pt-BR")]
    [InlineData("Renomeie a primeira das duas para Corrida", "pt-BR")]
    public async Task SelectionReplay_SingularWriteToOneOfDistinctTitlesAsksWhateverSetOrOrderWordTheMessageHas(
        string message, string language)
    {
        var search = language == "en" ? "Walk" : "Caminhada";
        Habit[] habits = language == "en"
            ? [CreateHabit("Morning Walk"), CreateHabit("Evening Walk")]
            : [CreateHabit("Caminhada manha"), CreateHabit("Caminhada noite")];

        var run = await RunSelectionTurnAsync(message, search, language, habits,
            [UpdateCall("rename", habits[..1], new { title = "Run" })]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().BeEmpty("the reply was: " + run.Result.Value.AiMessage);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        run.Result.Value.AiMessage.Should().Be(SelectionQuestionFor(search, language));
    }

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "en")]
    [InlineData(false, "pt-BR")]
    [InlineData(true, "pt-BR")]
    public async Task SelectionReplay_SettledSameTitleGroupLeavesTheOtherTitleGuarded(bool sameRound, string language)
    {
        var (search, other, message) = language == "en"
            ? ("Walk", "Morning Walk", "Rename both Walk habits to Run")
            : ("Caminhada", "Caminhada manha", "Renomeie ambos os hábitos Caminhada para Corrida");
        var habits = new[] { CreateHabit(search), CreateHabit(search), CreateHabit(other) };
        var settled = UpdateCall("both", habits[..2], new { title = "Run" });
        var guessed = UpdateCall("guess", habits[2..], new { title = "Run" });

        var run = sameRound
            ? await RunSelectionTurnAsync(message, search, language, habits, [settled, guessed])
            : await RunSelectionTurnAsync(message, search, language, habits, [settled], [guessed]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().ContainSingle().Which.Arguments.GetProperty("filter").GetProperty("habit_ids").EnumerateArray()
            .Select(id => id.GetGuid()).Should().BeEquivalentTo(habits[..2].Select(habit => habit.Id));
        run.Result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo(habits[..2].Select(habit => habit.Id));
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(outcome => outcome.Id == "guess" && !outcome.Success)),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
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
