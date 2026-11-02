using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ConversationPlanTests
{
    [Theory]
    [InlineData("rejected", "skipped")]
    [InlineData("approved", "done")]
    [InlineData("approved", "failed")]
    public async Task Plan_ReloadTracksActualPreviewAndExecutionOutcomes(string decision, string finalStatus)
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture();
        var capture = fixture.Capture();
        var pending = new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings()), fixture.Flags);
        const string arguments = "{\"title\":\"Read\",\"due_date\":\"2026-12-31\"}";
        var fingerprint = AgentOperationFingerprint.Compute("create_habit", arguments);
        var proposal = pending.Create(fixture.User.Id, new AgentCatalogService().GetCapabilityByChatTool("create_habit")!,
            "create_habit", arguments, "Read", fingerprint, AgentExecutionSurface.Chat, new(fixture.ConversationId, capture.CreatedAtUtc));
        pending.Revise(fixture.User.Id, proposal.Id, fingerprint, arguments, fingerprint, "preview").Should().BeTrue();
        var plan = AstraPlan.Create("Reading", [new("1", "Prepare a preview", "done", "create_habit", "execution", "write"),
            new("2", "Save reading", "pending", "create_habit", "approval", "write", proposal.Id)]).Value;
        await fixture.Store.RecordAsync(capture with { ResponseJson = JsonSerializer.Serialize(new ChatResponse(null, [], Plan: plan), AstraPlan.JsonOptions) });
        (await Reload()).Steps.Last().Status.Should().Be("pending");
        if (decision == "rejected")
            pending.Cancel(fixture.User.Id, proposal.Id, fingerprint).Should().BeTrue();
        else
        {
            pending.Confirm(fixture.User.Id, proposal.Id).Should().NotBeNull();
            (await Reload()).Steps.Last().Status.Should().Be("running", "approval does not prove the write succeeded");
            var store = new ConversationPlanStore(fixture.Db, new UnitOfWork(fixture.Db, new DatabaseConnectionSettings()), fixture.Flags);
            (await store.ChangeAsync(fixture.User.Id, proposal.Id, finalStatus))!.Steps.Last().Status.Should().Be(finalStatus);
        }
        var final = await Reload();
        final.Id.Should().Be(plan.Id);
        final.Steps.Select(step => step.Status).Should().Equal("done", finalStatus);
        var row = await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(row => row.Role == "assistant");
        AstraPlan.Read(row.ResponseJson).Should().BeEquivalentTo(final);

        async Task<AstraPlan> Reload()
        {
            var page = await fixture.Store.GetLatestAsync(fixture.User.Id, fixture.ConversationId, null, 10);
            return page!.Turns.Single(turn => turn.Role == "assistant").Plan!;
        }
    }

    [Fact]
    public async Task Plan_RecordingOffDoesNotStoreContentOrUpdatePriorTurns()
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture(enabled: false);
        var plan = AstraPlan.Create("Private plan", [new("1", "Read habits", "done", "query_habits", "execution"),
            new("2", "Read goals", "done", "query_goals", "execution")]).Value;
        var capture = fixture.Capture() with { ResponseJson = JsonSerializer.Serialize(new ChatResponse(null, [], Plan: plan), AstraPlan.JsonOptions) };
        (await fixture.Store.RecordAsync(capture)).Should().BeFalse();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
        var store = new ConversationPlanStore(fixture.Db, new UnitOfWork(fixture.Db, new DatabaseConnectionSettings()), fixture.Flags);
        (await store.ChangeAsync(fixture.User.Id, Guid.NewGuid(), "done")).Should().BeNull();
        fixture.Flags.GetEnabledKeysForUserAsync(fixture.User.Id, Arg.Any<CancellationToken>(), true).Returns([FeatureFlagKeys.AstraConversationRecording]);
        (await store.ChangeAsync(Guid.NewGuid(), Guid.NewGuid(), "done")).Should().BeNull();
    }
}
