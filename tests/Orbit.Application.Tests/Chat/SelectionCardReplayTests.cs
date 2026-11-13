using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The five selection cards that the staging runs of thomasluizon/orbit-tickets#2129 recorded for "delete two of my
/// six Water - 710ml habits and rename the other four". Each turn runs the production read tool, the real ask tool
/// and the real selection guard, with the model rounds recorded as replies.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private const string OwnerSplitMessagePt = "eu tenho 7 habitos chamados Water - 710ml deleta dois desses, e os outros 4 coloca Water - 1.18L";
    private const string OwnerSplitMessageEn = "I have 7 habits called Water - 710ml delete two of them, and rename the other 4 to Water - 1.18L";

    /// <summary>
    /// The ask arguments of a recorded card. Run 1 has no stream left, so its card follows the ticket comment: a
    /// single-select choice with 4 record options and a compound question. Runs 3 to 5 copy the streams.
    /// </summary>
    private static object RecordedCard(int run, Habit[] children, bool portuguese) => run switch
    {
        1 => new
        {
            missing_input = "record_selection",
            form = "choice",
            multiSelect = false,
            allowFreeText = false,
            question = portuguese ? "Quais dois excluir e quais quatro renomear?" : "Which two to delete and which four to rename?",
            options = children[..4].Select((child, index) => new { id = child.Id.ToString(), label = $"{index + 1}º" }).ToArray()
        },
        3 => new
        {
            missing_input = "record_selection",
            form = "choice",
            multiSelect = false,
            allowFreeText = true,
            question = portuguese ? "Quais dois hábitos Water - 710ml você quer excluir?" : "Which two Water - 710ml habits do you want to delete?",
            options = new object[]
            {
                new { id = "setA", label = portuguese ? "Primeiros quatro" : "First four", description = portuguese ? "2 excluir, 2 renomear" : "2 delete, 2 rename" },
                new { id = "setB", label = portuguese ? "Últimos quatro" : "Last four", description = portuguese ? "2 excluir, 2 renomear" : "2 delete, 2 rename" },
                new { id = "all6", label = portuguese ? "Os seis" : "All six", description = portuguese ? "diga quais dois excluir" : "say which two to delete" }
            }
        },
        4 => new
        {
            missing_input = "bulk_selection",
            form = "choice",
            multiSelect = true,
            allowFreeText = false,
            question = portuguese ? "Encontrei 6 hábitos Water - 710ml, não 7. Quais dois você quer excluir?" : "I found 6 Water - 710ml habits, not 7. Which two to delete?",
            options = Enumerable.Range(1, 4).Select(index => new { id = $"habit{index}", label = $"{index}º", description = "Water - 710ml" }).ToArray()
        },
        _ => new
        {
            missing_input = "record_selection",
            form = "choice",
            multiSelect = false,
            allowFreeText = true,
            question = portuguese ? "Quais 2 dos 6 Water - 710ml devo excluir? Indique as posições." : "Which 2 of the 6 Water - 710ml should I delete?",
            options = new object[]
            {
                new { id = "first", label = portuguese ? "Os dois primeiros" : "The first two" },
                new { id = "last", label = portuguese ? "Os dois últimos" : "The last two" }
            }
        }
    };

    private sealed record SelectionCardRun(Result<ChatResponse> Result, Habit[] Children, List<IssuedAiBlock> Issued,
        List<IReadOnlyList<AiToolCallResult>> ToolResults);

    /// <summary>
    /// The read search and the request of a selection card turn, with the number of "Water - 710ml" sub-habits.
    /// Without a message, the turn sends the owner's split request.
    /// </summary>
    private sealed record SelectionCardTurn(string Search, string? Message = null, int Children = 6);

    private Task<SelectionCardRun> RunSelectionCardAsync(string language, string[] partKinds,
        params Func<Habit[], object>[] cards) =>
        RunSelectionCardAsync(language, partKinds, new SelectionCardTurn("Water 710ml"), cards);

    /// <summary>
    /// One turn over a parent "Water" with same-titled "Water - 710ml" sub-habits. The first round reads the habits;
    /// each later round sends one recorded ask.
    /// </summary>
    private async Task<SelectionCardRun> RunSelectionCardAsync(string language, string[] partKinds, SelectionCardTurn turn,
        params Func<Habit[], object>[] cards)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var (_, children) = SetupSameTitleChildren("Water - 710ml", turn.Children);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, RealAskTool());
        HoldSelectionReplayWrites(query);
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(query.Name, "lookup", JsonSerializer.SerializeToElement(new { search = turn.Search, orbit_read_complete = false }))]
        });
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        var replies = cards.Select((card, index) => Result.Success(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new("ask", $"ask{index}", JsonSerializer.SerializeToElement(new { input = card(children) }))]
        }))
            .Append(Result.Success(new AiResponse { TextMessage = language == "en" ? "Which ones?" : "Quais?" })).ToArray();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(replies[0], replies[1..]);
        var result = await handler.Handle(new ProcessUserChatCommand(UserId,
            turn.Message ?? (language == "en" ? OwnerSplitMessageEn : OwnerSplitMessagePt),
            ClientContext: new(SupportsParts: true, PartKinds: partKinds, SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true, Locale: language)), default);
        return new SelectionCardRun(result, children, issued, toolResults);
    }

    /// <summary>
    /// The records that a stored card lets the person select, and whether it takes more than one selection.
    /// </summary>
    private static (string[] Records, bool Several) Selectable(AiBlockDefinition definition) => definition.Form switch
    {
        AiBlockKinds.Choice => ([.. definition.Options.SelectMany(AiChoiceSelection.TargetsOf).Distinct()], definition.MultiSelect),
        AiBlockKinds.Question => ([.. definition.Questions!.Single().Options!.Select(option => option.Id)],
            definition.Questions!.Single().Type == "multipleChoice"),
        _ => ([], true)
    };

    [Theory]
    [InlineData(1, "en")]
    [InlineData(1, "pt-BR")]
    [InlineData(3, "en")]
    [InlineData(3, "pt-BR")]
    [InlineData(4, "en")]
    [InlineData(4, "pt-BR")]
    [InlineData(5, "en")]
    [InlineData(5, "pt-BR")]
    public async Task SelectionCard_RecordedCardReachesThePersonOnlyWhenItHoldsEveryCandidateAndSeveralSelections(int run, string language)
    {
        var portuguese = language == "pt-BR";
        var card = await RunSelectionCardAsync(language, ["habitList", "choice", "question"],
            children => RecordedCard(run, children, portuguese));

        card.Result.IsSuccess.Should().BeTrue(card.Result.Error);
        var block = card.Issued.Should().ContainSingle("the card reached the person").Subject.ReadDefinition();
        var (records, several) = Selectable(block);
        records.Should().BeEquivalentTo(card.Children.Select(child => child.Id.ToString()),
            "each of the six candidates must be selectable");
        several.Should().BeTrue("the request names two records to delete");
        card.Result.Value.Parts.Should().ContainSingle(part => part is ChatBlockPart);
        if (block.Form == AiBlockKinds.Question)
            block.Questions!.Single().Options!.Select(option => option.Label).Should().OnlyHaveUniqueItems()
                .And.AllSatisfy(label => label.Should().StartWith("Water - 710ml"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task SelectionCard_OptionIdThatNoReadReturnedIsRejectedWithActionableText(string language)
    {
        var portuguese = language == "pt-BR";
        object Card(Habit[] children, bool invent) => new
        {
            missing_input = "record_selection",
            form = "question",
            questions = new[]
            {
                new
                {
                    id = "deletar", type = "singleChoice", allowFreeText = false,
                    prompt = portuguese ? "Quais dois Water - 710ml você quer excluir?" : "Which two Water - 710ml do you want to delete?",
                    options = children.Select((child, index) => new
                    {
                        id = invent && index == 5 ? "9b610c68-0080-417a-80b1-a3f9c3fbc810" : child.Id.ToString(),
                        label = $"{index + 1}º"
                    }).ToArray()
                }
            }
        };

        var run = await RunSelectionCardAsync(language, ["habitList", "choice", "question"],
            children => Card(children, invent: true), children => Card(children, invent: false));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var rejected = run.ToolResults[1].Should().ContainSingle(result => result.Name == "ask").Subject;
        rejected.Success.Should().BeFalse();
        rejected.Error.Should().Contain("query_habits returned in this turn").And.NotContain("plain text");
        var block = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        var (records, several) = Selectable(block);
        records.Should().BeEquivalentTo(run.Children.Select(child => child.Id.ToString()));
        several.Should().BeTrue();
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task SelectionCard_ChoiceOnlyClientGetsACoveringSetCardOrAnActionableRejection(string language, bool withTargets)
    {
        var portuguese = language == "pt-BR";
        object Card(Habit[] children) => new
        {
            missing_input = "record_selection",
            form = "choice",
            allowFreeText = true,
            question = portuguese ? "Quais 2 devo excluir?" : "Which 2 should I delete?",
            options = new object[]
            {
                new { id = "first", label = portuguese ? "Os dois primeiros" : "The first two",
                    target_ids = withTargets ? children[..2].Select(child => child.Id.ToString()).ToArray() : null },
                new { id = "middle", label = portuguese ? "Os do meio" : "The middle two",
                    target_ids = withTargets ? children[2..4].Select(child => child.Id.ToString()).ToArray() : null },
                new { id = "last", label = portuguese ? "Os dois últimos" : "The last two",
                    target_ids = withTargets ? children[4..].Select(child => child.Id.ToString()).ToArray() : null }
            }
        };

        var run = await RunSelectionCardAsync(language, ["habitList", "choice"], Card);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        if (withTargets)
        {
            var (records, _) = Selectable(run.Issued.Should().ContainSingle().Subject.ReadDefinition());
            records.Should().BeEquivalentTo(run.Children.Select(child => child.Id.ToString()));
            return;
        }
        run.Issued.Should().BeEmpty("a card whose options name no records cannot hold the answer");
        var rejected = run.ToolResults[1].Should().ContainSingle(result => result.Name == "ask").Subject;
        rejected.Error.Should().Contain("target_ids").And.NotContain("plain text");
    }

    /// <summary>
    /// "Delete Water" over a parent "Water" and six "Water - 710ml" sub-habits: the model asks "the parent or a
    /// sub-habit?". The options name the parent and one sub-habit, or name no record. The card that the server
    /// completes keeps every record the person can mean.
    /// </summary>
    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task SelectionCard_CompletedCardKeepsTheParentThatTheCardOrTheSelectionOffered(string language, bool optionsNameRecords)
    {
        var portuguese = language == "pt-BR";
        object Card(Habit[] children) => new
        {
            missing_input = "record_selection",
            form = "choice",
            allowFreeText = false,
            question = portuguese ? "O hábito Water ou um sub-hábito?" : "The Water habit or a sub-habit?",
            options = optionsNameRecords
                ? new object[]
                {
                    new { id = children[0].ParentHabitId!.Value.ToString(), label = "Water" },
                    new { id = children[2].Id.ToString(), label = "Water - 710ml" }
                }
                : new object[]
                {
                    new { id = "parent", label = portuguese ? "O hábito pai" : "The parent" },
                    new { id = "sub", label = portuguese ? "Um sub-hábito" : "A sub-habit" }
                }
        };

        var run = await RunSelectionCardAsync(language, ["habitList", "choice", "question"],
            new SelectionCardTurn("Water", portuguese ? "Exclua Water" : "Delete Water"), Card);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var block = run.Issued.Should().ContainSingle("the card reached the person").Subject.ReadDefinition();
        var (records, several) = Selectable(block);
        records.Should().BeEquivalentTo([run.Children[0].ParentHabitId!.Value.ToString(), .. run.Children.Select(child => child.Id.ToString())],
            "the parent that the person can mean stays selectable beside each sub-habit");
        several.Should().BeFalse("the request names one habit");
        block.Questions!.Single().Options!.Select(option => option.Label).Should().OnlyHaveUniqueItems()
            .And.Contain("Water");
    }

    /// <summary>
    /// A card for same-titled records that no parent, start date, emoji or tag tells apart. Each option carries the
    /// place of its record in the read, as the server-completed card does, whatever order the model wrote.
    /// </summary>
    [Theory]
    [InlineData("en", "{0} of 3")]
    [InlineData("pt-BR", "{0}º de 3")]
    public async Task SelectionCard_RecordsThatNoDetailTellsApartCarryTheirPlaceInTheRead(string language, string position)
    {
        var portuguese = language == "pt-BR";
        object Card(Habit[] children) => new
        {
            missing_input = "record_selection",
            form = "choice",
            allowFreeText = false,
            question = portuguese ? "Qual Water - 710ml devo excluir?" : "Which Water - 710ml should I delete?",
            options = new[] { children[2], children[0], children[1] }
                .Select(child => new { id = child.Id.ToString(), label = "Water - 710ml" }).ToArray()
        };

        var run = await RunSelectionCardAsync(language, ["habitList", "choice", "question"],
            new SelectionCardTurn("Water 710ml", portuguese ? "Exclua um Water - 710ml" : "Delete a Water - 710ml", Children: 3), Card);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var block = run.Issued.Should().ContainSingle().Subject.ReadDefinition();
        block.Form.Should().Be(AiBlockKinds.Choice);
        block.Options.Select(option => (option.Id, option.Description)).Should().Equal(
            (run.Children[2].Id.ToString(), string.Format(System.Globalization.CultureInfo.InvariantCulture, position, 3)),
            (run.Children[0].Id.ToString(), string.Format(System.Globalization.CultureInfo.InvariantCulture, position, 1)),
            (run.Children[1].Id.ToString(), string.Format(System.Globalization.CultureInfo.InvariantCulture, position, 2)));
    }

    /// <summary>
    /// Stores an issued block that the person answers in the turn.
    /// </summary>
    private IssuedAiBlock StoreAnsweredBlock(AiBlockDefinition definition)
    {
        var block = IssuedAiBlock.Create(UserId, definition);
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => { block.MarkAnswered(call.ArgAt<DateTime>(2)); return true; });
        _blockStore.CheckpointAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return block;
    }

    /// <summary>
    /// The block as the server stores it after it completed a card for six same-titled records: one question that
    /// takes several selections, with one option for each record.
    /// </summary>
    private IssuedAiBlock StoredRecordQuestion(Habit[] children, string prompt) =>
        StoreAnsweredBlock(new AiBlockDefinition(AiBlockKinds.Question, string.Empty, [], [],
            Questions: [new AiQuestion("records", "multipleChoice", prompt,
                [.. children.Select((child, index) => new AiQuestionOption(child.Id.ToString(), $"{child.Title} ({index + 1} of 6)"))])]));

    private static object DeleteHabitCompletes(Guid habitId) => new { habit_id = habitId, orbit_write_complete = true };

    [Theory]
    [InlineData("en", "Which two do you want to delete?")]
    [InlineData("pt-BR", "Quais dois você quer excluir?")]
    public async Task SelectionCard_AnsweredRecordQuestionSettlesBothSetsAndReachesOnePreview(string language, string prompt)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _unitOfWork.PassThroughTransactions<Result<PreparedAiBlockAnswer>>();
        var (_, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(delete, update);
        var held = HoldReplayWrites(delete, update);
        var block = StoredRecordQuestion(children, prompt);
        AiToolRequest? sent = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(value => sent = value),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(SplitWrites(children)));
        ReplyToReplay("Review the changes.");

        var result = await handler.Handle(new(UserId, "Answer", BlockAnswer: new(block.Id, [],
                Answers: [new AiQuestionAnswer("records", [.. children[4..].Select(child => child.Id.ToString())])]),
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice", "question"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        sent!.DynamicContext.Should().Contain("Validated choice selection: ");
        held.Select(request => request.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"]);
        result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo(children.Select(child => child.Id));
    }

    [Theory]
    [InlineData("en", "Which 2 should I delete?")]
    [InlineData("pt-BR", "Quais 2 dos 6 Water - 710ml devo excluir?")]
    public async Task SelectionCard_RefusedWriteForOneAnsweredSetKeepsTheTurnOpenUntilTheOtherActionIsPrepared(string language, string question)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _unitOfWork.PassThroughTransactions<Result<PreparedAiBlockAnswer>>();
        var (_, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(query, delete, update);
        var held = HoldSelectionReplayWrites(query, delete, update);
        var foreign = Guid.NewGuid();
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
                request.Arguments.GetRawText().Contains(foreign.ToString())), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult("bulk_delete_habits", "bulk_delete_habits",
                AgentRiskClass.Destructive, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Denied,
                PolicyReason: "target_not_owned:bulk_delete_habits:habit")));
        var definition = JsonSerializer.Deserialize<AiBlockDefinition>(JsonSerializer.Serialize(new
        {
            Form = "choice",
            Title = question,
            Options = new object[]
            {
                new { Id = "first", Label = language == "en" ? "The first two" : "Os dois primeiros", target_ids = children[..2].Select(child => child.Id.ToString()) },
                new { Id = "last", Label = language == "en" ? "The last two" : "Os dois últimos", target_ids = children[4..].Select(child => child.Id.ToString()) }
            },
            Items = Array.Empty<object>(),
            AllowFreeText = true
        }))!;
        var block = StoreAnsweredBlock(definition);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new(delete.Name, "delete-guess", JsonSerializer.SerializeToElement(new { habit_ids = new[] { foreign, children[5].Id } })),
                new(query.Name, "reread", ParseArguments("""{"for_selection":false,"include_sub_habits":true,"orbit_read_complete":false}"""))
            ]
        });
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls = [new(update.Name, "rename", JsonSerializer.SerializeToElement(new
                    {
                        filter = new { habit_ids = children[..4].Select(child => child.Id) },
                        updates = new { title = "Water - 1.18L" }, orbit_write_complete = true
                    }))]
            }),
                Result.Success(new AiResponse
                {
                    ConversationContext = TestConversationContext,
                    ToolCalls = [new(delete.Name, "delete", JsonSerializer.SerializeToElement(new
                    {
                        habit_ids = children[4..].Select(child => child.Id), orbit_write_complete = true
                    }))]
                }),
                Result.Success(new AiResponse { TextMessage = "Review the changes." }));

        var result = await handler.Handle(new(UserId, "Answer", BlockAnswer: new(block.Id, ["last"]),
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var renameResult = toolResults.Should().HaveCountGreaterThan(1).And.Subject.ElementAt(1)
            .Should().ContainSingle(item => item.Id == "rename").Subject;
        JsonSerializer.Serialize(renameResult).Should().Contain(children[4].Id.ToString()).And.Contain(children[5].Id.ToString());
        held.Where(request => request.OperationId == delete.Name)
            .Should().ContainSingle("the remaining delete is prepared in the same turn");
        result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo(children.Select(child => child.Id));
    }

    /// <summary>
    /// "Which Walk should I delete?" answered with the first Walk: one action for one record. A refused write of the
    /// turn names a record that no read returned, or names both Walks in one change. Neither is a separate change to
    /// the Walk the person turned down, so the delete of the chosen Walk completes the turn alone.
    /// </summary>
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task SelectionCard_RefusedWriteThatMakesNoSeparateChangeToTheOtherSetLeavesAOneActionAnswerComplete(
        string language, bool refusalNamesBothWalks)
    {
        var portuguese = language == "pt-BR";
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _unitOfWork.PassThroughTransactions<Result<PreparedAiBlockAnswer>>();
        var title = portuguese ? "Caminhada" : "Walk";
        var walks = new[] { CreateHabit(title), CreateHabit(title) };
        SetupSelectionHabits(walks);
        var delete = new DeleteHabitTool(_mediator, _habitRepo);
        var bulkDelete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var handler = CreateHandler(delete, bulkDelete);
        HoldReplayWrites(delete, bulkDelete);
        var foreign = Guid.NewGuid();
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
                request.Arguments.GetRawText().Contains(foreign.ToString())), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(bulkDelete.Name, bulkDelete.Name,
                AgentRiskClass.Destructive, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Denied,
                PolicyReason: "target_not_owned:bulk_delete_habits:habit")));
        var block = StoreAnsweredBlock(new AiBlockDefinition(AiBlockKinds.Choice,
            portuguese ? "Qual Caminhada devo excluir?" : "Which Walk should I delete?",
            [.. walks.Select((walk, index) => new AiBlockOption(walk.Id.ToString(), title, portuguese ? $"{index + 1}º de 2" : $"{index + 1} of 2"))], []));
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(bulkDelete.Name, "delete-guess", JsonSerializer.SerializeToElement(new
            {
                habit_ids = refusalNamesBothWalks ? new[] { foreign, walks[0].Id, walks[1].Id } : [foreign]
            }))]
        });
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls = [new(delete.Name, "delete", JsonSerializer.SerializeToElement(DeleteHabitCompletes(walks[0].Id)))]
            }),
                Result.Success(new AiResponse
                {
                    ConversationContext = TestConversationContext,
                    ToolCalls = [new(delete.Name, "delete-turned-down", JsonSerializer.SerializeToElement(DeleteHabitCompletes(walks[1].Id)))]
                }),
                Result.Success(new AiResponse { TextMessage = "Review the changes." }));

        var result = await handler.Handle(new(UserId, "Answer", BlockAnswer: new(block.Id, [walks[0].Id.ToString()]),
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        toolResults.SelectMany(round => round).Select(item => JsonSerializer.Serialize(item))
            .Should().NotContain(item => item.Contains("remaining_answered_records"));
        result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().Equal([walks[0].Id], "the person chose the first Walk and turned the other down");
    }
}
