using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// A turn revises a preview from an earlier turn and prepares two writes, and the turn combines the three into one
/// change set (thomasluizon/orbit-tickets#2129). The store of these tests is the real store, so a change set reserves
/// its members and closes them when it closes, as in production.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    /// <summary>
    /// The real pending operation store over <paramref name="database"/> answers each call to the store of the turn. The
    /// database holds the person, because the store reads the person before it binds a preview to a recorded conversation.
    /// </summary>
    private PendingAgentOperationStore AnswerFromTheRealStore(OrbitDbContext database)
    {
        var person = User.Create("Reader", "reader@example.test").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(person, UserId);
        database.Users.Add(person);
        database.SaveChanges();
        var store = new PendingAgentOperationStore(database, Options.Create(new AgentPlatformSettings()));
        _pendingOperationStore.Create(UserId, Arg.Any<AgentCapability>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<AgentExecutionSurface>(), Arg.Any<PendingOperationConversation?>())
            .Returns(call => store.Create(UserId, call.ArgAt<AgentCapability>(1), call.ArgAt<string>(2), call.ArgAt<string>(3),
                call.ArgAt<string>(4), call.ArgAt<string>(5), call.ArgAt<AgentExecutionSurface>(6),
                call.ArgAt<PendingOperationConversation?>(7)));
        _pendingOperationStore.GetExecution(UserId, Arg.Any<Guid>()).Returns(call => store.GetExecution(UserId, call.ArgAt<Guid>(1)));
        _pendingOperationStore.IsAwaitingConfirmation(UserId, Arg.Any<Guid>())
            .Returns(call => store.IsAwaitingConfirmation(UserId, call.ArgAt<Guid>(1)));
        _pendingOperationStore.GetAwaitingAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => store.GetAwaitingAsync(UserId));
        _pendingOperationStore.GetOpenIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => store.GetOpenIdsAsync(UserId));
        _pendingOperationStore.GetRevision(UserId, Arg.Any<Guid>()).Returns(call => store.GetRevision(UserId, call.ArgAt<Guid>(1)));
        _pendingOperationStore.Revise(UserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<PendingOperationChangePreview?>())
            .Returns(call => store.Revise(UserId, call.ArgAt<Guid>(1), call.ArgAt<string>(2), call.ArgAt<string>(3),
                call.ArgAt<string>(4), call.ArgAt<string>(5), call.ArgAt<PendingOperationChangePreview?>(6)));
        _pendingOperationStore.Restore(UserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<PendingAgentOperationRevision>())
            .Returns(call => store.Restore(UserId, call.ArgAt<Guid>(1), call.ArgAt<string>(2), call.ArgAt<PendingAgentOperationRevision>(3)));
        _pendingOperationStore.Cancel(UserId, Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(call => store.Cancel(UserId, call.ArgAt<Guid>(1), call.ArgAt<string>(2)));
        _pendingOperationStore.Withdraw(UserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>?>())
            .Returns(call => store.Withdraw(UserId, call.ArgAt<Guid>(1), call.ArgAt<string>(2), call.ArgAt<IReadOnlyCollection<Guid>?>(3)));
        return store;
    }

    /// <summary>Creates a row in <paramref name="store"/> and binds its preview, as the executor does for a held write.</summary>
    private static PendingAgentOperation StoreHeldWrite(PendingAgentOperationStore store, string operationId, string arguments,
        string previewFingerprint)
    {
        var fingerprint = AgentOperationFingerprint.Compute(operationId, arguments);
        var pending = store.Create(UserId, BuildCapability(operationId), operationId, arguments, operationId, fingerprint,
            AgentExecutionSurface.Chat);
        store.Revise(UserId, pending.Id, fingerprint, arguments, fingerprint, previewFingerprint).Should().BeTrue();
        return pending with { PreviewFingerprint = previewFingerprint };
    }

    /// <summary>Each write of <paramref name="tools"/> that the executor holds prepares its row in <paramref name="store"/>.</summary>
    private void HoldWritesIn(PendingAgentOperationStore store, params IAiTool[] tools)
    {
        var previewer = new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, new AiToolRegistry(tools));
        var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => names.Contains(request.OperationId)),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<AgentExecuteOperationRequest>();
                var preview = (await previewer.PreviewAsync(UserId, request.OperationId, request.Arguments)).Value!;
                var pending = StoreHeldWrite(store, request.OperationId, request.Arguments.GetRawText(), preview.PreviewFingerprint!);
                return new AgentExecuteOperationResponse(new AgentOperationResult(request.OperationId, request.OperationId,
                        AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation),
                    PendingOperation: pending with { Changes = preview.Changes, ChangeTargetCount = preview.ChangeTargetCount, Items = preview.Items });
            });
    }

    /// <summary>A preview from an earlier turn renames Water 3 and awaits approval in <paramref name="store"/>.</summary>
    private static PendingAgentOperation StoreEarlierPreview(PendingAgentOperationStore store, Habit[] children) =>
        StoreHeldWrite(store, "update_habit", EarlierPreviewArguments(children, EarlierTitle).GetRawText(), "earlier-preview");

    /// <summary>The revision of this turn renames <paramref name="earlier"/> to <paramref name="title"/> in place and keeps its id.</summary>
    private void ReviseInPlace(PendingAgentOperationStore store, PendingAgentOperation earlier, Habit[] children, string title)
    {
        var before = EarlierPreviewArguments(children, EarlierTitle).GetRawText();
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == ReviseTool),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var revised = EarlierPreviewArguments(children, title).GetRawText();
                store.Revise(UserId, earlier.Id, AgentOperationFingerprint.Compute("update_habit", before), revised,
                    AgentOperationFingerprint.Compute("update_habit", revised), "revised-preview").Should().BeTrue();
                var preview = earlier with
                {
                    ChangeTargetCount = 1,
                    PreviewFingerprint = "revised-preview",
                    Items = [new PendingOperationItem("0", children[2].Id, children[2].Title, [], "state")]
                };
                return new AgentExecuteOperationResponse(new AgentOperationResult(ReviseTool, ReviseTool, AgentRiskClass.Low,
                    AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: preview));
            });
    }

    private static AiToolCall ReviseStoredPreview(Guid id, string title) => new(ReviseTool, "revise", JsonSerializer.SerializeToElement(new
    {
        pending_operation_id = id,
        items = new[] { new { item_id = "0", edits_json = JsonSerializer.Serialize(new { title }) } },
        answer_question = false
    }));

    /// <summary>
    /// An answer turn reads two "Run" records that need a choice, revises the earlier preview, and prepares the bulk rename
    /// and the delete. The three combine into one change set, and the continuation fails. The selection question shows no
    /// preview of the turn, so the turn withdraws its writes. The earlier preview gets its state from before the turn back
    /// and stays approvable on its own, also when the revision kept its title and so its fingerprint.
    /// </summary>
    [Theory]
    [InlineData("en", RevisedTitle)]
    [InlineData("pt-BR", RevisedTitle)]
    [InlineData("en", EarlierTitle)]
    public async Task DeniedWrite_ASelectionAfterACombinedRevisionGivesTheEarlierPreviewItsStateBack(string language, string title)
    {
        using var database = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase("CombinedPreviews_" + Guid.NewGuid()).Options);
        var store = AnswerFromTheRealStore(database);
        PendingAgentOperation? earlier = null;
        PendingAgentOperationRevision? revisionBefore = null;
        var runs = new[] { new HabitListCardItem(Guid.NewGuid().ToString(), "Run", null, 0, false, "none"),
            new HabitListCardItem(Guid.NewGuid().ToString(), "Run", null, 0, false, "none") };

        var run = await RunDeniedAnswerTurnAsync(language, _ => [], children =>
            {
                HoldWritesIn(store, new BulkDeleteHabitsTool(_mediator, _habitRepo), new BulkUpdateHabitsTool(_mediator));
                ReviseInPlace(store, earlier!, children, title);
                _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == "query_habits"),
                        Arg.Any<CancellationToken>())
                    .Returns(new AgentExecuteOperationResponse(new AgentOperationResult("query_habits", "query_habits",
                        AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded,
                        Payload: new HabitListCard("all", runs) { Selection = new HabitSelectionCandidates("Run", runs, RequiresAnswer: true) })));
            },
            first: children =>
            {
                earlier = StoreEarlierPreview(store, children);
                revisionBefore = store.GetRevision(UserId, earlier.Id);
                return Calls(new AiToolCall("query_habits", "runs", JsonSerializer.SerializeToElement(new { search = "Run", for_selection = true })),
                    ReviseStoredPreview(earlier.Id, title), CorrectedRename(children), Run4Delete(children));
            },
            lastReply: FailedReply("unavailable"),
            tools: [ReadTool(ReviseTool, new ToolResult(true)), ReadTool("query_habits", new ToolResult(true)),
                new UpdateHabitTool(_habitRepo, _userDateService, _mediator)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().Be(SelectionQuestionFor("Run", language));
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        var rows = await database.PendingAgentOperations.AsNoTracking().ToListAsync();
        var changeSet = rows.Should().ContainSingle(row => row.OperationId == AccountChangeSet.OperationName,
            "the turn combined its revision and its two writes").Subject;
        AccountChangeSet.Reservations(JsonDocument.Parse(changeSet.ArgumentsJson).RootElement)
            .Select(member => member.PendingOperationId).Should().HaveCount(3).And.Contain(earlier!.Id);
        changeSet.ConsumedAtUtc.Should().NotBeNull("the turn withdrew the change set");
        rows.Where(row => row.OperationId is "bulk_update_habits" or "bulk_delete_habits")
            .Should().HaveCount(2).And.OnlyContain(row => row.ConsumedAtUtc != null, "the turn withdrew its two writes");
        store.IsAwaitingConfirmation(UserId, earlier.Id).Should().BeTrue("the earlier preview stays approvable");
        store.GetRevision(UserId, earlier.Id).Should().Be(revisionBefore, "the earlier preview has its state from before the turn");
        store.Confirm(UserId, earlier.Id).Should().NotBeNull("no change set holds the earlier preview");
    }

    /// <summary>
    /// The same revision and two writes, and a concurrent write makes the store refuse the bind of their change set. The
    /// turn withdraws its two writes, and the earlier preview gets its state from before the turn back and stays approvable.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AFailedChangeSetBindGivesTheEarlierPreviewItsStateBack(string language)
    {
        var binding = new ConcurrentChangeSetBind();
        using var database = binding.CreateContext();
        var store = AnswerFromTheRealStore(database);
        PendingAgentOperation? earlier = null;
        PendingAgentOperationRevision? revisionBefore = null;

        var run = await RunDeniedAnswerTurnAsync(language, _ => [Text(language)], children =>
            {
                HoldWritesIn(store, new BulkDeleteHabitsTool(_mediator, _habitRepo), new BulkUpdateHabitsTool(_mediator));
                ReviseInPlace(store, earlier!, children, RevisedTitle);
            },
            first: children =>
            {
                earlier = StoreEarlierPreview(store, children);
                revisionBefore = store.GetRevision(UserId, earlier.Id);
                return Calls(ReviseStoredPreview(earlier.Id, RevisedTitle), CorrectedRename(children), Run4Delete(children));
            },
            tools: [ReadTool(ReviseTool, new ToolResult(true)), new UpdateHabitTool(_habitRepo, _userDateService, _mediator)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().Be(CommittedFor(language));
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the turn withdrew the two writes that its checkpoint holds");
        var rows = await database.PendingAgentOperations.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle(row => row.OperationId == AccountChangeSet.OperationName, "the turn tried to combine")
            .Which.ConsumedAtUtc.Should().NotBeNull("the store refused the bind, so the change set closed");
        rows.Where(row => row.OperationId is "bulk_update_habits" or "bulk_delete_habits")
            .Should().HaveCount(2).And.OnlyContain(row => row.ConsumedAtUtc != null, "the turn withdrew its two writes");
        store.IsAwaitingConfirmation(UserId, earlier!.Id).Should().BeTrue("the earlier preview stays approvable");
        store.GetRevision(UserId, earlier.Id).Should().Be(revisionBefore, "the earlier preview has its state from before the turn");
        store.Confirm(UserId, earlier.Id).Should().NotBeNull("no change set holds the earlier preview");
    }

    /// <summary>
    /// The same answer turn without the selection read: the revision and the two writes combine, and the continuation
    /// fails, so the block answer replays the checkpoint with the two writes. No answer shows the change set, so the turn
    /// withdraws it and releases its members: each replayed card and the earlier preview stay approvable.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AReplayAfterACombinedRevisionKeepsEachReplayedPreviewApprovable(string language)
    {
        using var database = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase("CombinedReplay_" + Guid.NewGuid()).Options);
        var store = AnswerFromTheRealStore(database);
        PendingAgentOperation? earlier = null;

        var run = await RunDeniedAnswerTurnAsync(language, _ => [], children =>
            {
                HoldWritesIn(store, new BulkDeleteHabitsTool(_mediator, _habitRepo), new BulkUpdateHabitsTool(_mediator));
                ReviseInPlace(store, earlier!, children, RevisedTitle);
            },
            first: children =>
            {
                earlier = StoreEarlierPreview(store, children);
                return Calls(ReviseStoredPreview(earlier.Id, RevisedTitle), CorrectedRename(children), Run4Delete(children));
            },
            lastReply: FailedReply("unavailable"),
            tools: [ReadTool(ReviseTool, new ToolResult(true)), new UpdateHabitTool(_habitRepo, _userDateService, _mediator)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().Be(CommittedFor(language));
        var replayed = run.Result.Value.PendingOperations.Should().HaveCount(2, "the checkpoint holds the two writes").And.Subject;
        var changeSet = (await database.PendingAgentOperations.AsNoTracking().ToListAsync())
            .Should().ContainSingle(row => row.OperationId == AccountChangeSet.OperationName, "the turn combined its previews").Subject;
        changeSet.ConsumedAtUtc.Should().NotBeNull("no answer shows the change set, so the turn withdrew it");
        foreach (var preview in replayed)
            store.Confirm(UserId, preview.Id).Should().NotBeNull("the person can approve each replayed card");
        store.Confirm(UserId, earlier!.Id).Should().NotBeNull("no change set holds the earlier preview");
    }
}
