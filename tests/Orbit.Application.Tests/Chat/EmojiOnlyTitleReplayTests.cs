using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// A habit title of emoji only has no letter or digit. Each check that compares, groups or finds a title keeps two
/// different such titles apart, and a title with no key is never found in a message.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private static readonly AgentClientContext RecordCardClient = new(SupportsParts: true, PartKinds: ["habitList", "choice"],
        SupportsHabitListCard: true, SupportsDatedHabitListCard: true);

    /// <summary>
    /// A bulk write over records that the turn did not read waits for a read only when two of them share a title.
    /// </summary>
    [Theory]
    [InlineData("🏃", "📚", true)]
    [InlineData("Walk", "WALK", false)]
    public async Task EmojiOnlyTitles_AnUnreadBulkWriteOverTwoOfThemIsNoSameTitledSet(string first, string second, bool prepared)
    {
        Habit[] habits = [CreateHabit("Water"), CreateHabit(first), CreateHabit(second)];

        var (result, held) = await RunSelectionTurnAsync("Delete both of them.", "Water", "en", habits, [DeleteCall(habits[1..])]);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Select(request => request.OperationId).Should().Equal(prepared ? ["bulk_delete_habits"] : []);
    }

    /// <summary>
    /// An order word settles a selection only for a group of records that share a title. A record with an emoji-only
    /// title of its own waits for the answer.
    /// </summary>
    [Theory]
    [InlineData("🏃", "📚", false)]
    [InlineData("Walk", "Walk", true)]
    public async Task EmojiOnlyTitles_AnOrderWordDoesNotSettleASelectionOfTwoOfThem(string first, string second, bool prepared)
    {
        Habit[] habits = [CreateHabit(first), CreateHabit(second)];

        var (result, held) = await RunSelectionTurnAsync("Delete the last one.", AskSelectionRead(), "en", habits,
            [DeleteCall(habits[1..])]);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Select(request => request.OperationId).Should().Equal(prepared ? ["bulk_delete_habits"] : []);
    }

    /// <summary>
    /// A record card is completed with every member of the same-titled group that it names. Two emoji-only titles form
    /// no group, so the card keeps the options that the model gave.
    /// </summary>
    [Theory]
    [InlineData("🏃", "📚", 2)]
    [InlineData("Walk", "Walk", 3)]
    public async Task EmojiOnlyTitles_ARecordCardIsNotCompletedWithAnotherOfThem(string first, string second, int options)
    {
        Habit[] habits = [CreateHabit("Water"), CreateHabit(first), CreateHabit(second)];

        var block = await AskRecordChoiceAsync(habits, (habits[1], "First"), (habits[0], "Water"));

        block.Options.Should().HaveCount(options);
    }

    /// <summary>
    /// The options of records that share a title show the title and the place of each record in the group. Records
    /// with two different emoji-only titles keep the labels that the model gave, with no place.
    /// </summary>
    [Theory]
    [InlineData("🏃", "📚", false)]
    [InlineData("Walk", "Walk", true)]
    public async Task EmojiOnlyTitles_TheOptionsOfTwoOfThemAreNotPlacedInOneGroup(string first, string second, bool grouped)
    {
        Habit[] habits = [CreateHabit(first), CreateHabit(second)];

        var block = await AskRecordChoiceAsync(habits, (habits[0], "Run"), (habits[1], "Read"));

        block.Options.Select(option => option.Label).Should().Equal(grouped ? [first, second] : ["Run", "Read"]);
        block.Options.Select(option => option.Description).Should().BeEquivalentTo(grouped ? ["1 of 2", "2 of 2"] : [null, null]);
    }

    /// <summary>
    /// A write beside a record question runs when it touches no record that the question offers. An option label or a
    /// target name of emoji only names the record of that title alone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmojiOnlyTitles_AWriteBesideAQuestionAboutAnotherOfThemIsPrepared(bool targetByName)
    {
        SetupUserAndPayGate();
        var water = CreateHabit("Water");
        var run = CreateHabit("🏃");
        var read = CreateHabit("📚");
        SetupSelectionHabits(water, run, read);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var update = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var handler = CreateHandler(RealAskTool(), query, update);
        var held = HoldAskRerunWrites(query, update);
        var ask = new AiToolCall("ask", "which", JsonSerializer.SerializeToElement(new
        {
            form = "choice",
            missing_input = "record_selection",
            question = "Which habit would you like to skip?",
            options = new[]
            {
                new { id = run.Id.ToString(), label = targetByName ? "Run" : "🏃" },
                new { id = water.Id.ToString(), label = "Water" }
            },
            multiSelect = false,
            allowFreeText = false
        }));
        var rename = new AiToolCall("update_habit", "rename", JsonSerializer.SerializeToElement(new
        {
            habit_id = targetByName ? "📚" : read.Id.ToString(),
            title = "Books"
        }));
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new("query_habits", "all", JsonSerializer.SerializeToElement(new { for_selection = false })), ask, rename]
        });

        var result = await handler.Handle(new(UserId, "Rename 📚 to Books and skip one habit today.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle().Which.Arguments.GetProperty("habit_id").GetString().Should().Be(read.Id.ToString());
    }

    /// <summary>
    /// A phrase such as "sob o" asks for an existing parent only when a habit title follows it. An emoji-only title is
    /// found by its symbols, so it never matches every message.
    /// </summary>
    [Theory]
    [InlineData("Crie Leitura sob o sol da manhã.", true)]
    [InlineData("Crie Leitura sob o 🏃.", false)]
    [InlineData("Crie Leitura sob o \"🏃\".", false)]
    [InlineData("Crie Leitura sob o 🏃‍♀️.", true)]
    public async Task EmojiOnlyTitles_AnExistingParentIsRequestedOnlyByAPhraseWithItsSymbols(string message, bool prepared)
    {
        Habit[] habits = [.. SetupResolutionAccount(), CreateHabit("🏃")];
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Leitura"}]}"""));
        ReplyToReplay("Revise a prévia.");

        await handler.Handle(new(UserId, message), default);

        held.Should().HaveCount(prepared ? 1 : 0);
    }

    /// <summary>The habit targets share a title only when their title keys are equal.</summary>
    [Theory]
    [InlineData("🏃", "📚", false)]
    [InlineData("Walk", "WALK", true)]
    public void EmojiOnlyTitles_TwoOfThemAreNoSharedTarget(string first, string second, bool shared)
    {
        Habit[] habits = [CreateHabit(first), CreateHabit(second)];

        HabitTargetContext.Build(habits, habits).HasSharedTargets.Should().Be(shared);
    }

    /// <summary>A parent name offers the habits of its own title, never a habit with another emoji-only title.</summary>
    [Theory]
    [InlineData("🏃", "🏃")]
    [InlineData("ACADEMIA", "Academia")]
    public void EmojiOnlyTitles_AParentNameOffersOnlyTheHabitsOfItsOwnTitle(string name, string offered)
    {
        Habit[] habits = [CreateHabit("🏃"), CreateHabit("📚"), CreateHabit("Academia")];

        HabitHeadingParents.Candidates(name, habits).Select(habit => habit.Title).Should().Equal(offered);
    }

    /// <summary>
    /// An item belongs to the one heading whose text names its title. An emoji-only title is named by its symbols, so
    /// it finds its heading and never the heading of another emoji-only title.
    /// </summary>
    [Theory]
    [InlineData("📚", "Noite")]
    [InlineData("Alongar", "Manhã")]
    [InlineData("💧", null)]
    public void EmojiOnlyTitles_AnItemBelongsToTheHeadingThatNamesItsSymbols(string title, string? heading)
    {
        Habit[] habits = [CreateHabit("Manhã"), CreateHabit("Noite")];
        var headings = HabitHeadingParents.Read("Manhã: 🏃 e Alongar. Noite: 📚", habits);

        (HabitHeadingParents.ForItem(new System.Text.Json.Nodes.JsonObject { ["title"] = title }, headings)?.Name)
            .Should().Be(heading);
    }

    /// <summary>
    /// A placement destination is a habit that the message names. An emoji-only title is named only by its symbols.
    /// </summary>
    [Theory]
    [InlineData("Add Stretch to Academia", new[] { "Academia" })]
    [InlineData("Add Stretch to 🏃", new[] { "🏃" })]
    [InlineData("Add Stretch to 🏃‍♀️", new[] { "🏃‍♀️" })]
    public void EmojiOnlyTitles_APlacementDestinationIsOnlyAHabitThatTheMessageNames(string message, string[] destinations)
    {
        Habit[] habits = [CreateHabit("🏃"), CreateHabit("📚"), CreateHabit("Academia"), CreateHabit("🏃‍♀️")];

        var text = HabitPlacementContext.Build(habits, message);

        JsonDocument.Parse(text[text.IndexOf('{')..]).RootElement.GetProperty("destinations").EnumerateArray()
            .Select(destination => destination.GetProperty("title").GetString()).Should().Equal(destinations);
    }

    private async Task<AiChoiceBlock> AskRecordChoiceAsync(Habit[] habits, params (Habit Habit, string Label)[] options)
    {
        SetupUserAndPayGate();
        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        var ask = new AiToolCall("ask", "which", JsonSerializer.SerializeToElement(new
        {
            form = "choice",
            missing_input = "record_selection",
            question = "Which habit?",
            options = options.Select(option => new { id = option.Habit.Id.ToString(), label = option.Label }),
            multiSelect = false,
            allowFreeText = false
        }));
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [AskSelectionRead(), ask] });

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: RecordCardClient), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Block).OfType<AiChoiceBlock>().Single();
    }
}
