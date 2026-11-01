using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class ConversationTurnRecorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewOutcomes_IndependentContextsWithStaleSnapshots_PreserveBothApprovals(bool insertAfterApprovals)
    {
        using var fixture = new Fixture();
        var capture = fixture.Capture();
        var pending = new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings()), fixture.Flags);
        var proposals = new[] { "Reservation", "Travel" }.Select(title =>
        {
            var arguments = JsonSerializer.Serialize(new { title, due_date = "2026-12-31" });
            var fingerprint = Orbit.Domain.Common.AgentOperationFingerprint.Compute("create_habit", arguments);
            var proposal = pending.Create(fixture.User.Id, new AgentCatalogService().GetCapabilityByChatTool("create_habit")!,
                "create_habit", arguments, title, fingerprint, AgentExecutionSurface.Chat, new(fixture.ConversationId, capture.CreatedAtUtc));
            pending.Revise(fixture.User.Id, proposal.Id, fingerprint, arguments, fingerprint, "preview").Should().BeTrue();
            return proposal;
        }).ToArray();
        if (!insertAfterApprovals) await fixture.Store.RecordAsync(capture);
        using var first = fixture.Database.CreateContext();
        using var second = fixture.Database.CreateContext();
        if (!insertAfterApprovals)
        {
            first.ConversationTurns.Single(turn => turn.Role == "assistant").PreviewOutcomesJson.Should().Contain("pending");
            second.ConversationTurns.Single(turn => turn.Role == "assistant").PreviewOutcomesJson.Should().Contain("pending");
        }
        var firstStore = new PendingAgentOperationStore(first, Options.Create(new AgentPlatformSettings()), fixture.Flags);
        var secondStore = new PendingAgentOperationStore(second, Options.Create(new AgentPlatformSettings()), fixture.Flags);

        firstStore.Confirm(fixture.User.Id, proposals[0].Id).Should().NotBeNull();
        secondStore.Confirm(fixture.User.Id, proposals[1].Id).Should().NotBeNull();
        if (insertAfterApprovals) await fixture.Store.RecordAsync(capture);

        var stored = await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(turn => turn.Role == "assistant");
        JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(stored.PreviewOutcomesJson)!
            .Should().HaveCount(2).And.OnlyContain(preview => preview.Outcome == "approved");
        (await fixture.Db.PendingAgentOperations.AsNoTracking().ToListAsync()).Should()
            .OnlyContain(state => state.ConfirmedAtUtc != null);
    }

    [Fact]
    public async Task FlagOff_PendingTransitionsAndRecording_DoNotReadOrUpdateStoredTurns()
    {
        var commands = new CountingDbCommandInterceptor();
        using var fixture = new Fixture(interceptor: commands);
        var capture = fixture.Capture();
        await fixture.Store.RecordAsync(capture);
        fixture.Flags.ResolveForUser(fixture.User.Id).Returns([new(FeatureFlagKeys.AstraConversationRecording, false, null)]);
        fixture.Flags.GetEnabledKeysForUserAsync(fixture.User.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        commands.Reset();
        var pending = new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings()), fixture.Flags);
        const string arguments = "{\"title\":\"Reservation\"}";
        var fingerprint = Orbit.Domain.Common.AgentOperationFingerprint.Compute("create_habit", arguments);
        var proposal = pending.Create(fixture.User.Id, new AgentCatalogService().GetCapabilityByChatTool("create_habit")!,
            "create_habit", arguments, "Reservation", fingerprint, AgentExecutionSurface.Chat,
            new(fixture.ConversationId, capture.CreatedAtUtc));
        pending.Revise(fixture.User.Id, proposal.Id, fingerprint, arguments, fingerprint, "preview").Should().BeTrue();
        pending.Confirm(fixture.User.Id, proposal.Id).Should().NotBeNull();
        (await fixture.Store.RecordAsync(capture with { UserContent = "", ToolCallsJson = "invalid" })).Should().BeFalse();

        commands.Commands.Should().NotContain(command => command.Sql.Contains("ConversationTurns", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Record_WritesBothRowsBeforeReturning_EncryptsContentAndSummaries()
    {
        using var fixture = new Fixture();
        var capture = fixture.Capture() with
        {
            ToolResults = [new("get_habits", "one", true, null, null, null, new { title = "Private habit", due_date = "2026-12-31" })],
            ResponseJson = "{\"parts\":[{\"type\":\"text\",\"text\":\"Private composition\"}]}"
        };
        (await fixture.Store.RecordAsync(capture)).Should().BeTrue();
        var rows = await fixture.Db.ConversationTurns.OrderBy(turn => turn.TurnOrder).ToListAsync();
        rows.Select(turn => turn.Role).Should().Equal("user", "assistant");
        rows.Select(turn => turn.TurnOrder).Should().Equal(1, 2);
        rows[1].ToolResultsJson.Should().Contain("Private habit").And.Contain("2026-12-31");
        await using var command = fixture.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Content, ToolResultsJson, ResponseJson FROM ConversationTurns WHERE Role = 'assistant'";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        for (var index = 0; index < 3; index++) reader.GetString(index).Should().StartWith("enc:");
    }

    [Fact]
    public async Task Record_FlagOff_WritesNothing()
    {
        using var fixture = new Fixture(enabled: false);
        (await fixture.Store.RecordAsync(fixture.Capture())).Should().BeFalse();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Record_SaveFailure_FailsRequestAndRemovesUploadedImage()
    {
        var failure = new FailingSave();
        using var fixture = new Fixture(interceptor: failure);
        failure.Enabled = true;
        Func<Task> action = () => fixture.Store.RecordAsync(fixture.Capture(image: true));
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Storage unavailable");
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
        await fixture.Storage.Received(1).DeletePrivateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task History_ImageOnlyWithinLastThreeExchanges_AndLatestPageUsesDisplayOrder()
    {
        using var fixture = new Fixture();
        await fixture.Store.RecordAsync(fixture.Capture(image: true));
        var history = await fixture.Store.LoadHistoryAsync(fixture.User.Id, fixture.ConversationId);
        history.Value.Messages[0].ImageData.Should().Equal(Fixture.Image);
        for (var index = 0; index < 3; index++) await fixture.Store.RecordAsync(fixture.Capture());
        history = await fixture.Store.LoadHistoryAsync(fixture.User.Id, fixture.ConversationId);
        history.Value.Messages.Should().OnlyContain(message => message.ImageData == null);
        var latest = await fixture.Store.GetLatestAsync(fixture.User.Id, null, null, 3);
        latest!.Turns.Select(turn => turn.Order).Should().Equal(6, 7, 8);
        latest.BeforeOrder.Should().Be(6);
        var previous = await fixture.Store.GetLatestAsync(fixture.User.Id, latest.ConversationId, latest.BeforeOrder, 3);
        previous!.Turns.Select(turn => turn.Order).Should().Equal(3, 4, 5);
    }

    [Fact]
    public async Task Delete_IsAccountScoped_RemovesRowsImagesAndPreventsLateSave()
    {
        using var fixture = new Fixture();
        await fixture.Store.RecordAsync(fixture.Capture(image: true));
        var other = Guid.NewGuid();
        (await fixture.Store.DeleteAsync(other, fixture.ConversationId)).Should().BeFalse();
        (await fixture.Store.LoadHistoryAsync(other, fixture.ConversationId)).IsFailure.Should().BeTrue();
        (await fixture.Store.GetLatestAsync(other, fixture.ConversationId, null, 10)).Should().BeNull();
        await fixture.Storage.DidNotReceiveWithAnyArgs().DeleteConversationObjectsAsync(default);
        (await fixture.Store.DeleteAsync(fixture.User.Id, fixture.ConversationId)).Should().BeTrue();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
        await fixture.Storage.Received(1).DeleteConversationObjectsAsync(fixture.User.Id, fixture.ConversationId, Arg.Any<CancellationToken>());
        (await fixture.Store.RecordAsync(fixture.Capture() with { ExistingConversation = true })).Should().BeFalse();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Reset_RemovesImagesAndRows_RejectsInFlightContent()
    {
        using var fixture = new Fixture();
        var capture = fixture.Capture(image: true);
        await fixture.Store.RecordAsync(capture);
        var reset = new AccountResetRepository(fixture.Db, Substitute.For<Orbit.Domain.Events.IAccountEventCollector>(), fixture.Storage);
        await reset.DeleteAllUserDataAsync(fixture.User.Id);
        fixture.User.ResetAccount();
        await fixture.Db.SaveChangesAsync();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
        await fixture.Storage.Received(1).DeleteConversationObjectsAsync(fixture.User.Id, null, Arg.Any<CancellationToken>());
        (await fixture.Store.RecordAsync(capture with { TurnId = Guid.NewGuid() })).Should().BeFalse();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task History_KeepsOldConversationsWithoutExpiry_CompactsLargeResultsWithRetainedData()
    {
        using var fixture = new Fixture();
        await fixture.Store.RecordAsync(fixture.Capture() with
        {
            CreatedAtUtc = DateTime.UtcNow.AddDays(-90),
            ToolResults = [new("get_habits", "one", true, null, null, null,
                Enumerable.Range(0, 1000).Select(index => new { title = $"habit {index}", due_date = "2026-12-31" }).ToArray())]
        });
        var history = await fixture.Store.LoadHistoryAsync(fixture.User.Id, fixture.ConversationId);
        var assistant = history.Value.Messages.Single(message => message.Role == "assistant").Content;
        assistant.Should().Contain("truncated").And.Contain("2026-12-31").And.Contain("has_more");
        (await fixture.Db.ConversationTurns.SingleAsync(turn => turn.Role == "assistant")).ToolResultsJson.Length.Should().BeLessThanOrEqualTo(12_002);
    }

    [Fact]
    public async Task FailedConfirmation_DoesNotPersistAnApprovedOutcomeOnALaterSave()
    {
        var conflict = new ConfirmationConflict();
        using var fixture = new Fixture(interceptor: conflict);
        var capture = fixture.Capture();
        var pending = new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings()), fixture.Flags);
        var catalog = new AgentCatalogService();
        const string arguments = "{\"title\":\"Reservation\",\"due_date\":\"2026-12-31\"}";
        var fingerprint = Orbit.Domain.Common.AgentOperationFingerprint.Compute("create_habit", arguments);
        var proposal = pending.Create(fixture.User.Id, catalog.GetCapabilityByChatTool("create_habit")!, "create_habit",
            arguments, "Reservation", fingerprint, AgentExecutionSurface.Chat, new(fixture.ConversationId, capture.CreatedAtUtc));
        pending.Revise(fixture.User.Id, proposal.Id, fingerprint, arguments, fingerprint, "preview").Should().BeTrue();
        await fixture.Store.RecordAsync(capture);
        conflict.FailNext = true;
        pending.Confirm(fixture.User.Id, proposal.Id).Should().BeNull();
        await fixture.Db.SaveChangesAsync();
        var stored = await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(turn => turn.Role == "assistant");
        var outcome = JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(stored.PreviewOutcomesJson)!.Single();
        outcome.Outcome.Should().Be("pending");
    }

    private sealed class ConfirmationConflict : SaveChangesInterceptor
    {
        internal bool FailNext { get; set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (!FailNext) return result;
            FailNext = false;
            throw new DbUpdateConcurrencyException("Confirmation conflict");
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal static readonly byte[] Image = [1, 2, 3, 4];
        internal SqliteOrbitDbContextFactory Database { get; }
        internal OrbitDbContext Db => Database.Context;
        internal User User { get; }
        internal Guid ConversationId { get; } = Guid.NewGuid();
        internal IObjectStorageService Storage { get; } = Substitute.For<IObjectStorageService>();
        internal IFeatureFlagService Flags { get; } = Substitute.For<IFeatureFlagService>();
        internal ConversationTurnRecorder Store { get; }
        internal Fixture(bool enabled = true, IInterceptor? interceptor = null)
        {
            var encryption = new EncryptionService(Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(new byte[32]) }), NullLogger<EncryptionService>.Instance);
            Database = new SqliteOrbitDbContextFactory(encryption, interceptor is null ? [] : [interceptor]);
            User = User.Create("Person", "recording@example.com").Value;
            Db.Users.Add(User);
            Db.SaveChanges();
            Flags.GetEnabledKeysForUserAsync(User.Id, Arg.Any<CancellationToken>()).Returns(enabled ? new[] { FeatureFlagKeys.AstraConversationRecording } : []);
            Flags.ResolveForUser(User.Id).Returns([new(FeatureFlagKeys.AstraConversationRecording, enabled, null)]);
            Storage.ReadPrivateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Image);
            Store = new(Db, new UnitOfWork(Db, new DatabaseConnectionSettings()), Flags, encryption, Storage, Options.Create(new AiSettings { Model = "recording-model" }));
        }
        internal ConversationTurnCapture Capture(bool image = false) => new(User.Id, Guid.NewGuid(), DateTime.UtcNow,
            "User private message", "Assistant private reply", "[]", true, ConversationId: ConversationId,
            ImageData: image ? Image : null, ImageMimeType: image ? "image/png" : null);
        public void Dispose() => Database.Dispose();
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        internal bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new InvalidOperationException("Storage unavailable");
            return ValueTask.FromResult(result);
        }
    }
}
