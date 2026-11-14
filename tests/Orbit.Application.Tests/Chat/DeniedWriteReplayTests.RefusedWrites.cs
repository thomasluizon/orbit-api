using System.Text.Json;
using FluentAssertions;
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
}
