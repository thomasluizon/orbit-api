using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// A guard of the handler turns a write down before dispatch (thomasluizon/orbit-tickets#2129). The end of the tool loop
/// compares each refused write with every write of the same operation that the turn prepared, in any round.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private static string RenameTwoAndDeleteMessage(string language) => language == "en"
        ? "rename Water 1 and Water 2 to Water - 1.18L, and delete Water 5 and Water 6"
        : "muda o nome do Water 1 e do Water 2 para Water - 1.18L, e apaga o Water 5 e o Water 6";

    private static string RenameThreeMessage(string language) => language == "en"
        ? "rename Water 1, Water 2 and Water 3 to Water - 1.18L"
        : "muda o nome do Water 1, do Water 2 e do Water 3 para Water - 1.18L";

    private static AiToolCall BulkRenameOf(string id, IEnumerable<Habit> habits, object updates) => new("bulk_update_habits", id,
        JsonSerializer.SerializeToElement(new
        {
            filter = new { all = false, habit_ids = habits.Select(habit => habit.Id), is_completed = false },
            updates
        }));

    private static IEnumerable<Guid> PreparedIds(DeniedWriteRun run) =>
        run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty().And.Subject
            .SelectMany(operation => operation.Items!).Select(item => item.EntityId ?? Guid.Empty);

    /// <summary>
    /// The handler turns down the bulk rename of Water 1 and Water 2, because it also changes the schedule, and the delete
    /// prepares. The next round renames each record on its own. Neither rename holds both records, but together they
    /// carry the refused rename, so the turn holds the whole request.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_LaterWritesThatCarryARefusedWriteTogetherKeepThePreview(string language)
    {
        var run = await RunRequestTurnAsync(language, RenameTwoAndDeleteMessage(language),
            children => Calls(Run4Delete(children), BulkRenameOf("rename-both", children[..2], new { title = RenameTitle, frequency_unit = "Week" })),
            children => [Calls(Rename("rename-one", children[0].Id.ToString()), Rename("rename-two", children[1].Id.ToString())), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults[0].Should().ContainSingle(result => result.Id == "rename-both").Which.Success.Should().BeFalse();
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        PreparedIds(run).Should().Contain([run.Children[0].Id, run.Children[1].Id, run.Children[4].Id, run.Children[5].Id]);
    }

    /// <summary>
    /// The first round prepares the bulk rename of Water 1 to 3. The model then repeats it as three single renames, which
    /// the handler turns down as repeated single writes, and answers in words. The earlier bulk rename already carries
    /// the three refused renames, so the turn keeps its preview.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AnEarlierWriteThatCarriesTheRefusedWritesKeepsThePreview(string language)
    {
        var run = await RunRequestTurnAsync(language, RenameThreeMessage(language),
            children => Calls(BulkRenameOf("rename-bulk", children[..3], new { title = RenameTitle })),
            children => [Calls(Rename("rename-one", children[0].Id.ToString()), Rename("rename-two", children[1].Id.ToString()),
                Rename("rename-three", children[2].Id.ToString())), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults[1].Should().HaveCount(3).And.OnlyContain(result => !result.Success);
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        PreparedIds(run).Should().BeEquivalentTo(run.Children[..3].Select(child => child.Id));
    }

    /// <summary>A creation that the habit proposal check turns down: an avoid habit with no recurrence.</summary>
    private static AiToolCall RefusedCreation(string title) => new("create_habit", "create-" + title,
        JsonSerializer.SerializeToElement(new { title, frequency_unit = "Day", frequency_quantity = 1, is_bad_habit = true, is_general = true }));

    private static AiToolCall Creation(string title) => new("create_habit", "created-" + title,
        JsonSerializer.SerializeToElement(new { title, frequency_unit = "Day", frequency_quantity = 1 }));

    /// <summary>
    /// The request creates Water and Tea. The proposal check turns the Water creation down, and the next round creates
    /// only Tea. A creation names the habit that it makes by its title, so the Tea creation does not carry the refused
    /// Water creation, and the turn holds no preview with only Tea. A later creation of Water carries it.
    /// </summary>
    [Theory]
    [InlineData("Tea", false)]
    [InlineData("Water", true)]
    public async Task DeniedWrite_ARefusedCreationIsCarriedOnlyByACreationOfTheSameHabit(string later, bool carried)
    {
        SetupUserAndPayGate();
        var create = RecoveryCreateHabitTool();
        var handler = CreateHandler(create);
        var gateway = InstallCorrectionGateway(create, hold: true);
        SetupAiResponse(Calls(RefusedCreation("Water")));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Calls(Creation(later))), Result.Success(Text("en")), Result.Success(Text("en")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create a Water habit and a Tea habit."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        gateway.Requests.Should().ContainSingle("the proposal check turned the first Water creation down before dispatch")
            .Which.Arguments.GetProperty("title").GetString().Should().Be(later);
        if (carried)
            result.Value.PendingOperations.Should().NotBeNullOrEmpty("the later creation makes the refused habit");
        else
        {
            result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only Tea holds half of the request");
            result.Value.AiMessage.Should().Be(NotPreparedFor("en"));
        }
    }

    /// <summary>
    /// The proposal check turns a creation down, and the next round creates another habit. A creation with no title names
    /// no habit, and a title of emoji only names its habit by the text as written, so no creation of another habit
    /// carries the refused creation and the turn holds no preview. A later creation with the same emoji title carries it.
    /// </summary>
    [Theory]
    [InlineData(null, "Tea", false)]
    [InlineData("💧", "🍵", false)]
    [InlineData("💧", "💧", true)]
    public async Task DeniedWrite_ARefusedCreationIsCarriedOnlyByACreationWithItsOwnTitle(string? refused, string later, bool carried)
    {
        SetupUserAndPayGate();
        var create = RecoveryCreateHabitTool();
        var handler = CreateHandler(create);
        var gateway = InstallCorrectionGateway(create, hold: true);
        SetupAiResponse(Calls(refused is null
            ? new AiToolCall("create_habit", "create-untitled", JsonSerializer.SerializeToElement(new
            {
                name = "Water",
                frequency_unit = "Day",
                frequency_quantity = 1,
                is_bad_habit = true,
                is_general = true
            }))
            : RefusedCreation(refused)));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Calls(Creation(later))), Result.Success(Text("en")), Result.Success(Text("en")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create a Water habit and a Tea habit."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        gateway.Requests.Should().ContainSingle("the proposal check turned the first creation down before dispatch")
            .Which.Arguments.GetProperty("title").GetString().Should().Be(later);
        if (carried)
            result.Value.PendingOperations.Should().NotBeNullOrEmpty("the later creation makes the refused habit");
        else
        {
            result.Value.PendingOperations.Should().BeNullOrEmpty("the later creation makes another habit");
            result.Value.AiMessage.Should().Be(NotPreparedFor("en"));
        }
    }

    /// <summary>
    /// The proposal check turns down a creation of Morning with the sub-habit Stretch, and the next round sends the
    /// corrected creation. The preview has one item for Morning and holds Stretch inside it, so the prepared creation
    /// carries both refused titles and the turn keeps its preview. A corrected creation without Stretch carries no
    /// refused Stretch.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task DeniedWrite_ARefusedCreationWithASubHabitIsCarriedByTheCreationOfBoth(bool laterSubHabit, bool carried)
    {
        SetupUserAndPayGate();
        var create = RecoveryCreateHabitTool();
        var handler = CreateHandler(create);
        InstallCorrectionGateway(create, hold: true);
        SetupAiResponse(Calls(new AiToolCall("create_habit", "create-morning", JsonSerializer.SerializeToElement(new
        {
            title = "Morning",
            frequency_unit = "Day",
            frequency_quantity = 1,
            is_bad_habit = true,
            is_general = true,
            sub_habits = new[] { new { title = "Stretch" } }
        }))));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Calls(new AiToolCall("create_habit", "created-morning", laterSubHabit
                ? JsonSerializer.SerializeToElement(new
                {
                    title = "Morning",
                    frequency_unit = "Day",
                    frequency_quantity = 1,
                    sub_habits = new[] { new { title = "Stretch" } }
                })
                : JsonSerializer.SerializeToElement(new { title = "Morning", frequency_unit = "Day", frequency_quantity = 1 })))),
                Result.Success(Text("en")), Result.Success(Text("en")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create Morning with the sub-habit Stretch."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (carried)
            result.Value.PendingOperations.Should().NotBeNullOrEmpty("the later creation makes Morning and Stretch");
        else
            result.Value.AiMessage.Should().Be(NotPreparedFor("en"));
    }

    /// <summary>A bulk creation of <paramref name="count"/> Water habits.</summary>
    private static AiToolCall WaterCreations(string id, int count, bool refused) => new("bulk_create_habits", id,
        JsonSerializer.SerializeToElement(new
        {
            habits = Enumerable.Range(0, count).Select(_ => refused
                ? (object)new { title = "Water", frequency_unit = "Day", frequency_quantity = 1, is_bad_habit = true, is_general = true }
                : new { title = "Water", frequency_unit = "Day", frequency_quantity = 1 })
        }));

    /// <summary>
    /// The proposal check turns down a bulk creation of two Water habits, and the next round creates one Water habit or
    /// two. A creation is compared by the count of each title in the prepared preview. The handler keeps one Water habit
    /// of a pair and asks about the other, so no preview carries the refused pair, and the turn holds no preview.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeniedWrite_ARefusedCreationOfATitleTwiceIsCarriedOnlyByAsManyPreparedCreationsOfIt(int later)
    {
        SetupUserAndPayGate();
        var create = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(create);
        var held = HoldReplayWrites(create);
        SetupAiResponse(Calls(WaterCreations("create-pair", 2, refused: true)));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Calls(WaterCreations("created", later, refused: false))),
                Result.Success(Text("en")), Result.Success(Text("en")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create two Water habits."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle("the proposal check turned the pair down before dispatch")
            .Which.Arguments.GetProperty("habits").GetArrayLength().Should().Be(1, "the handler keeps one habit of a title");
        result.Value.PendingOperations.Should().BeNullOrEmpty("one Water habit holds half of the request");
        result.Value.AiMessage.Should().Be(NotPreparedFor("en"));
    }

    /// <summary>
    /// The proposal check turns the Water creation down, and the next round deletes another habit. No creation of the
    /// turn prepared, so nothing carries the refused creation, and the turn holds no preview with only the delete.
    /// </summary>
    [Fact]
    public async Task DeniedWrite_ARefusedCreationWithNoPreparedCreationLeavesNoPreview()
    {
        SetupUserAndPayGate();
        var run = CreateHabit("Run");
        SetupSelectionHabits([run]);
        var create = RecoveryCreateHabitTool();
        var delete = new DeleteHabitTool(_mediator, _habitRepo);
        var handler = CreateHandler(create, delete);
        var gateway = InstallCorrectionGateway(create, hold: true, delete);
        SetupAiResponse(Calls(RefusedCreation("Water")));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Calls(new AiToolCall(delete.Name, "delete-run", JsonSerializer.SerializeToElement(new { habit_id = run.Id })))),
                Result.Success(Text("en")), Result.Success(Text("en")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create a Water habit and delete Run."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        gateway.Requests.Should().ContainSingle("the proposal check turned the Water creation down before dispatch")
            .Which.OperationId.Should().Be(delete.Name);
        result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only the delete holds half of the request");
        result.Value.AiMessage.Should().Be(NotPreparedFor("en"));
    }
}
