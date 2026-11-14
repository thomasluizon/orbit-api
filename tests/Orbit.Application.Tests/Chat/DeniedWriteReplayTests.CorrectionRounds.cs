using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The rounds that choose a correction for a write that the ownership check turned down (thomasluizon/orbit-tickets#2129).
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    /// <summary>The rename of run 4 with its second id replaced by an id that no habit was copied from.</summary>
    private static AiToolCall UntracedRename(Habit[] children) => new("bulk_update_habits", "rename", JsonSerializer.SerializeToElement(new
    {
        filter = new { all = false, habit_ids = new[] { children[0].Id, UnknownHabitId }, is_completed = false },
        updates = new { title = RenameTitle }
    }));

    private static AiToolCall TagTheOthers(Habit[] children) => new("assign_tags", "tag",
        JsonSerializer.SerializeToElement(new { habit_ids = children[..4].Select(child => child.Id), tag_names = new[] { "Health" } }));

    /// <summary>
    /// The person picks the two records to delete, and the request renames the four others and tags them. The delete and
    /// the tag prepare. The rename names an id that no habit was copied from, so only the unchosen set of the choice links
    /// a resend to it. The tag changes that set too, but with another kind of change, so the resend on the set binds and
    /// the turn holds the whole request.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_Run4CorrectionOnTheOtherRecordsBindsBesideAnotherKindOfChangeToThem(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, children => [Calls(CorrectedRename(children)), Text(language)],
            first: children => Calls(UntracedRename(children), Run4Delete(children), TagTheOthers(children)),
            tools: [new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().Contain(request => request.OperationId == "assign_tags", "the tag prepared");
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        run.Held.Where(request => request.OperationId == "bulk_update_habits").Should().ContainSingle("the resend binds and prepares")
            .Which.Arguments.GetProperty("filter").GetProperty("habit_ids").EnumerateArray().Select(id => id.GetGuid())
            .Should().BeEquivalentTo(run.Children[..4].Select(child => child.Id));
        run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty();
    }
}
