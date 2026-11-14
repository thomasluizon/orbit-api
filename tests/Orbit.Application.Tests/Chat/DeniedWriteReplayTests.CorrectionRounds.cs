using System.Text.Json;
using FluentAssertions;
using NSubstitute;
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

    /// <summary>A read of Water 1 by an id of no record of the person.</summary>
    private static AiToolCall ReadUnknown() => new("query_habits", "read-unknown",
        JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId }));

    /// <summary>The ownership check turns down a read that names a habit id, with the reason the real check writes.</summary>
    private void DenyReadsById() =>
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == "query_habits"
                && request.Arguments.GetRawText().Contains("habit_id")), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult("query_habits", "query_habits", AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Denied, PolicyReason: "target_not_owned:query_habits:habit",
                Access: "read"), PolicyDenial: new AgentPolicyDenial("query_habits", "query_habits", AgentRiskClass.Low,
                AgentConfirmationRequirement.None, "target_not_owned:query_habits:habit")));

    /// <summary>
    /// One round reads Water 1 and renames it, each by an id of no record of the person, and nothing prepared. A read
    /// correction ends the turn after its reads and accepts no write, so it can never send the rename. The round fails
    /// at once with the tool failure answer, as a round with a read denial beside any other failure does.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AReadDenialBesideALoneWriteDenialFailsTheRoundAtOnce(string language)
    {
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language),
            children => Calls(ReadUnknown(), RenameOneUnknown(children)),
            children => [Calls(ReadWater(1)), Calls(RenameFirst(children)), Text(language)], _ => DenyReadsById());

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().Be(ToolFailureFor(language));
        run.ToolResults.Should().BeEmpty("the round offers no correction, so the model gets no further round");
        run.Held.Should().BeEmpty();
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
    }
}
