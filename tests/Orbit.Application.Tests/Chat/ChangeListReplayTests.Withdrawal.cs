using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// What a failed change list withdraws on the <c>ai_change_plan</c> path (thomasluizon/orbit-tickets#2160): only what
/// that list prepared. A preview that the person had before the turn, and a preview of an earlier round of the turn,
/// stay as they were. The store of these tests is the real store.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    /// <summary>
    /// The request holds no conversation id, so the store hands a write the row that already waits with the same
    /// arguments. The handler sorts the record ids of a call, so the earlier row holds them in that order. A list that
    /// repeats that change beside a failed change leaves the earlier row waiting for approval, and the answer says that
    /// the earlier change still waits.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR")]
    [InlineData(ChangeDeclarationShape.Wrapper, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_AFailedListKeepsAnEarlierPreviewThatTheStoreHandedToItsChange(ChangeDeclarationShape shape, string language)
    {
        PendingAgentOperation? earlier = null;
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, null, true, DeleteChange("delete", children[4..]), RenameChange("rename", [Guid.NewGuid()])),
            _ => [Text(language)],
            arrange: (store, children) => earlier = StoreHeldWrite(store, "bulk_delete_habits",
                JsonSerializer.Serialize(new { habit_ids = children[4..].Select(child => child.Id.ToString()).Order(StringComparer.Ordinal) }), "earlier-delete"));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().ContainSingle().Subject.Where(result => !result.Success).SelectMany(ErrorsOf).Distinct()
            .Should().ContainSingle().Which.Change.Should().Be("rename");
        (await run.Store.GetAwaitingAsync(UserId)).Select(pending => pending.PendingOperationId).Should().Equal([earlier!.Id],
            "the write found the earlier row, and a failed list never closes a preview that the person already had");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the failed list shows no card");
        run.Result.Value.AiMessage.Should().Be(EarlierKeptFor(language));
    }

    /// <summary>
    /// The first list of the turn prepares the deletes and does not end the turn. The second list fails, and the repair
    /// names only its own change. The preview of the first list stays, so the turn ends with the whole request.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR")]
    [InlineData(ChangeDeclarationShape.Wrapper, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_AFailedListKeepsThePreviewOfAnEarlierRoundAndItsRepairCompletesTheRequest(ChangeDeclarationShape shape, string language)
    {
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, null, false, DeleteChange("delete", children[4..])),
            children => [ChangeList(shape, null, true, RenameChange("rename", [children[0].Id, CutShort(children[1].Id), children[2].Id, children[3].Id])),
                ChangeList(shape, null, true, RenameChange("rename", children[..4].Select(child => (object)child.Id))), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().HaveCount(2, "the first list continues, the second list fails, and the repair ends the turn");
        run.ToolResults[1].Where(result => !result.Success).SelectMany(ErrorsOf).Distinct()
            .Should().ContainSingle("the error list names only the change of the failed list").Which.Change.Should().Be("rename");
        var preview = run.Result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.CapabilityId.Should().Be(AccountChangeSet.CapabilityId);
        preview.Items!.Where(item => item.Fields.Any(field => field.Field == "delete")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[4..].Select(child => child.Id), "the deletes of the first round stay");
        preview.Items!.Where(item => item.Fields.Any(field => field.Field == "title")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[..4].Select(child => child.Id));
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
    }

    /// <summary>
    /// A second failed list ends the turn with nothing, and that withdrawal takes the preview of the first round too,
    /// because the turn leaves part of the request.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_ASecondFailedListWithdrawsThePreviewOfAnEarlierRoundAndSaysSo(ChangeDeclarationShape shape, string language)
    {
        var failing = (Habit[] children) => ChangeList(shape, null, true, RenameChange("rename", [children[0].Id, CutShort(children[1].Id)]));
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, null, false, DeleteChange("delete", children[4..])),
            children => [failing(children), failing(children), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().HaveCount(2, "the model gets one repair round and no call after it");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty();
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }
}
