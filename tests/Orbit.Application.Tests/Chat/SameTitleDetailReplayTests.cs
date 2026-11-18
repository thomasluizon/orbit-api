using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// Chat turns over habits that share one title. The preview and the selection card give each record the place that
/// it has in the app list, and a client without the entity detail flag reads that place in the item name.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private static Habit PlacedHabit(string title, int position, Guid? parent = null) =>
        Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, title, null, null, DueDate: Today,
            ParentHabitId: parent, Position: position)).Value;

    private static AiToolCall ReadEachHabit() => new("query_habits", "lookup",
        JsonSerializer.SerializeToElement(new { orbit_read_complete = false }));

    [Theory]
    [InlineData("en", "Water (Morning, 2 of 2)", "Water (Morning, 1 of 2)")]
    [InlineData("pt-BR", "Water (Morning, 2º de 2)", "Water (Morning, 1º de 2)")]
    public async Task SameTitleDetail_TwoDeletesOfOneTitleGetDifferentNamesAndAUniqueTitleStaysPlain(string language,
        string later, string earlier)
    {
        var morning = PlacedHabit("Morning", 1);
        var laterWater = PlacedHabit("Water", 2, morning.Id);
        var earlierWater = PlacedHabit("Water", 1, morning.Id);
        var walk = PlacedHabit("Walk", 3, morning.Id);

        var (result, _) = await RunSelectionTurnAsync("Delete the two Water habits and Walk.", ReadEachHabit(), language,
            [morning, laterWater, earlierWater, walk], [DeleteCall([laterWater, earlierWater, walk])]);

        result.IsSuccess.Should().BeTrue(result.Error);
        var names = result.Value.PendingOperations!.SelectMany(operation => operation.Items!)
            .ToDictionary(item => item.EntityId!.Value, item => item.EntityName);
        names[laterWater.Id].Should().Be(later);
        names[earlierWater.Id].Should().Be(earlier);
        names[walk.Id].Should().Be("Walk");
    }

    /// <summary>
    /// The read returns the three records in the positions 3, 1 and 2. A card whose options name the records keeps
    /// the order of its options, and a card that the server completes lists the records in the app list order.
    /// </summary>
    [Theory]
    [InlineData("en", "{0} of 3", true)]
    [InlineData("pt-BR", "{0}º de 3", true)]
    [InlineData("en", "{0} of 3", false)]
    [InlineData("pt-BR", "{0}º de 3", false)]
    public async Task SameTitleDetail_CardPlaceIsThePlaceInTheAppListWhenTheReadOrderDiffers(string language, string place,
        bool optionsNameRecords)
    {
        var portuguese = language == "pt-BR";
        string Place(int index) => string.Format(System.Globalization.CultureInfo.InvariantCulture, place, index);
        object Card(Habit[] children) => new
        {
            missing_input = "record_selection",
            form = "choice",
            allowFreeText = false,
            question = portuguese ? "Qual Water - 710ml devo excluir?" : "Which Water - 710ml should I delete?",
            options = optionsNameRecords
                ? children.Select(child => new { id = child.Id.ToString(), label = "Water - 710ml" }).ToArray()
                : new object[]
                {
                    new { id = "first", label = portuguese ? "O primeiro" : "The first" },
                    new { id = "last", label = portuguese ? "O último" : "The last" }
                }
        };

        var run = await RunSelectionCardAsync(language, ["habitList", "choice", "question"],
            new SelectionCardTurn("Water 710ml", portuguese ? "Exclua um Water - 710ml" : "Delete a Water - 710ml",
                Children: 3, Positions: [3, 1, 2]), Card);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var options = run.Issued.Should().ContainSingle().Which.ReadDefinition().Options
            .Select(option => (option.Id, option.Description));
        if (optionsNameRecords)
            options.Should().Equal(
                (run.Children[0].Id.ToString(), Place(3)),
                (run.Children[1].Id.ToString(), Place(1)),
                (run.Children[2].Id.ToString(), Place(2)));
        else
            options.Should().Equal(
                (run.Children[1].Id.ToString(), Place(1)),
                (run.Children[2].Id.ToString(), Place(2)),
                (run.Children[0].Id.ToString(), Place(3)));
    }

    [Theory]
    [InlineData("en", "Water (Morning, 2 of 2)")]
    [InlineData("pt-BR", "Water (Morning, 2º de 2)")]
    public async Task SameTitleDetail_CreateItemThatReusesASameTitleHabitShowsTheDetail(string language, string name)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var morning = PlacedHabit("Morning", 1);
        var first = PlacedHabit("Water", 1, morning.Id);
        var reused = PlacedHabit("Water", 2, morning.Id);
        SetupSelectionHabits(morning, first, reused);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var create = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(query, create);
        HoldSelectionReplayWrites(query, create);
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [ReadEachHabit()] });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls = [new(create.Name, "create", JsonSerializer.SerializeToElement(new
                {
                    habits = new[]
                    {
                        new
                        {
                            title = "Water", habit_id = reused.Id, frequency_unit = "Day", frequency_quantity = 1,
                            sub_habits = new[] { new { title = "Sip" } }
                        }
                    }
                }))]
            }), Result.Success(new AiResponse { TextMessage = "Review the changes." }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Add a Sip sub-habit to my second Water.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var item = result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle().Subject;
        item.EntityId.Should().Be(reused.Id);
        item.EntityName.Should().Be(name);
        item.Fields.Single(field => field.Field == "title").NewValue.Should().Be("Water");
    }
    /// <summary>
    /// One turn that renames the second "Water" and deletes the first. The response for the client holds the detail.
    /// The conversation record, which is the source of the model history, the tool results that the model reads and
    /// the grounding facts hold the plain title and no text line.
    /// </summary>
    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task SameTitleDetail_RecordModelTextAndGroundingHoldThePlainTitleAndNoTextLine(string language, bool withFlag)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableRecording();
        ConversationTurnCapture? recorded = null;
        _conversationRecorder.RecordAsync(Arg.Do<ConversationTurnCapture>(turn => recorded = turn)).Returns(true);
        var morning = PlacedHabit("Morning", 1);
        var first = PlacedHabit("Water", 1, morning.Id);
        var second = PlacedHabit("Water", 2, morning.Id);
        SetupSelectionHabits(morning, first, second);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var handler = CreateHandler(query, delete, update);
        HoldSelectionReplayWrites(query, delete, update);
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [ReadEachHabit()] });
        var modelInputs = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => modelInputs.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls =
                [
                    new(update.Name, "rename", JsonSerializer.SerializeToElement(new { habit_id = second.Id, title = "Agua" })),
                    DeleteCall([first])
                ]
            }), Result.Success(new AiResponse { TextMessage = "Review the changes." }));
        var place = language == "en" ? "{0} of 2" : "{0}º de 2";
        string Detail(int index) => "Morning, " + string.Format(System.Globalization.CultureInfo.InvariantCulture, place, index);

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Rename my second Water to Agua and delete the first.",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true, SupportsPendingOperationEntityDetail: withFlag ? true : null)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var items = result.Value.PendingOperations!.SelectMany(operation => operation.Items!).ToDictionary(item => item.EntityId!.Value);
        items[second.Id].EntityName.Should().Be(withFlag ? "Water" : $"Water ({Detail(2)})");
        items[second.Id].EntityDetail.Should().Be(withFlag ? Detail(2) : null);
        items[first.Id].EntityName.Should().Be(withFlag ? "Water" : $"Water ({Detail(1)})");
        items[first.Id].EntityDetail.Should().Be(withFlag ? Detail(1) : null);
        items[second.Id].Fields.Single(field => field.Field == "title").NewValue.Should().Be("Agua");
        var answer = string.Join("\n", (result.Value.Parts?.OfType<ChatTextPart>().Select(part => part.Text) ?? [])
            .Append(result.Value.AiMessage));
        if (withFlag)
            answer.Should().NotContain("→");
        else
            answer.Split('\n').Where(text => text.Contains('→')).Should().Equal($"- Water ({Detail(2)}) → Agua");
        recorded.Should().NotBeNull();
        foreach (var stored in new[] { recorded!.ResponseJson!, recorded.AssistantContent ?? string.Empty, recorded.ToolCallsJson,
                     JsonSerializer.Serialize(recorded.ToolResults), JsonSerializer.Serialize(modelInputs) })
            stored.Should().NotContain("Morning, ", "no stored form and no model input holds the detail")
                .And.NotContain("\\u2192").And.NotContain("→").And.NotContainEquivalentOf("entityDetail");
        recorded.ResponseJson.Should().Contain("\"entityName\":\"Water\"");
        result.Value.GroundingFacts.Facts.Should().NotContain(fact => fact.Value.Contains("MORNING, ", StringComparison.OrdinalIgnoreCase));
        result.Value.GroundingFacts.Facts.Should().Contain(fact => fact.Value.Equals("WATER", StringComparison.OrdinalIgnoreCase));
    }
}
