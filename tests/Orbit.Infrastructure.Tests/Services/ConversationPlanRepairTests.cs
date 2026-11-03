using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ConversationPlanRepairTests
{
    [Theory]
    [InlineData("history", "start")]
    [InlineData("history", "commit")]
    [InlineData("history", "database_cancellation")]
    [InlineData("page", "start")]
    [InlineData("page", "commit")]
    [InlineData("page", "database_cancellation")]
    public async Task RepairFailure_ReturnsStoredReadAndCanRepairOnRetry(string read, string stage)
    {
        var failure = new RepairFailure(stage);
        var logger = new RepairLogger();
        using var fixture = new ConversationTurnRecorderTests.Fixture(interceptor: failure, logger: logger);
        var stored = await SeedRejectedPlanAsync(fixture);
        failure.Enabled = true;

        if (read == "history")
        {
            var result = await fixture.Store.LoadHistoryAsync(fixture.User.Id, fixture.ConversationId);
            result.IsSuccess.Should().BeTrue();
            result.Value.Messages.Select(message => message.Content).Should().Equal(stored.Select(ModelContent));
        }
        else
        {
            var page = await fixture.Store.GetLatestAsync(fixture.User.Id, fixture.ConversationId, null, 10);
            page!.Turns.Select(turn => turn.ResponseJson).Should().Equal(stored.Select(row => row.ResponseJson));
            page.Turns.Single(turn => turn.Role == "assistant").Plan!.Steps.Last().Status.Should().Be("pending");
        }

        failure.Attempts.Should().Be(1);
        var error = logger.Entries.Should().ContainSingle().Subject;
        error.Level.Should().Be(LogLevel.Error);
        error.Exception.Should().NotBeNull();
        error.Text.Should().Be($"Failed to reconcile conversation plans for user {fixture.User.Id} and turns {stored[1].TurnId}");
        var assistant = await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(row => row.Role == "assistant");
        assistant.ResponseJson.Should().Be(stored[1].ResponseJson);
        assistant.ModelContextJson.Should().Be(stored[1].ModelContextJson);
        failure.Enabled = false;
        var retry = await fixture.Store.GetLatestAsync(fixture.User.Id, fixture.ConversationId, null, 10);
        retry!.Turns.Single(turn => turn.Role == "assistant").Plan!.Steps.Last().Status.Should().Be("skipped");
        AstraPlan.Read((await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(row => row.Role == "assistant")).ResponseJson)!
            .Steps.Last().Status.Should().Be("skipped");
    }

    [Theory]
    [InlineData("history")]
    [InlineData("page")]
    public async Task Repair_RequestCancellationStillPropagates(string read)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new RepairFailure("commit", cancellation);
        var logger = new RepairLogger();
        using var fixture = new ConversationTurnRecorderTests.Fixture(interceptor: failure, logger: logger);
        var stored = await SeedRejectedPlanAsync(fixture);
        failure.Enabled = true;
        Func<Task> action = read == "history"
            ? async () => await fixture.Store.LoadHistoryAsync(fixture.User.Id, fixture.ConversationId, cancellation.Token)
            : async () => await fixture.Store.GetLatestAsync(fixture.User.Id, fixture.ConversationId, null, 10, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();

        failure.Attempts.Should().Be(1);
        cancellation.IsCancellationRequested.Should().BeTrue();
        logger.Entries.Should().BeEmpty();
        (await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(row => row.Role == "assistant"))
            .ResponseJson.Should().Be(stored[1].ResponseJson);
    }

    private static string ModelContent(ConversationTurn row) => row.Role == "assistant" ? row.ModelContextJson! : row.Content!;

    private static async Task<ConversationTurn[]> SeedRejectedPlanAsync(ConversationTurnRecorderTests.Fixture fixture)
    {
        var capture = fixture.Capture();
        var pending = new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings()), fixture.Flags);
        const string arguments = "{\"title\":\"Read\"}";
        var fingerprint = AgentOperationFingerprint.Compute("create_habit", arguments);
        var proposal = pending.Create(fixture.User.Id, new AgentCatalogService().GetCapabilityByChatTool("create_habit")!,
            "create_habit", arguments, "Reading", fingerprint, AgentExecutionSurface.Chat, new(fixture.ConversationId, capture.CreatedAtUtc));
        pending.Revise(fixture.User.Id, proposal.Id, fingerprint, arguments, fingerprint, "preview").Should().BeTrue();
        var plan = AstraPlan.Create("Reading", [new("1", "Prepare a preview", "done", "create_habit", "execution"),
            new("2", "Save reading", "pending", "create_habit", "approval", PendingOperationId: proposal.Id)]).Value;
        (await fixture.Store.RecordAsync(capture with
        { ResponseJson = JsonSerializer.Serialize(new ChatResponse(null, [], Plan: plan), AstraPlan.JsonOptions) })).Should().BeTrue();
        fixture.Flags.GetEnabledKeysForUserAsync(fixture.User.Id, Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(Array.Empty<string>());
        fixture.Flags.ResolveForUser(fixture.User.Id).Returns([new(FeatureFlagKeys.AstraConversationRecording, false, null)]);
        pending.Cancel(fixture.User.Id, proposal.Id, fingerprint).Should().BeTrue();
        fixture.Flags.GetEnabledKeysForUserAsync(fixture.User.Id, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns([FeatureFlagKeys.AstraConversationRecording]);
        fixture.Flags.ResolveForUser(fixture.User.Id).Returns([new(FeatureFlagKeys.AstraConversationRecording, true, null)]);
        var stored = await fixture.Db.ConversationTurns.AsNoTracking().OrderBy(row => row.TurnOrder).ToArrayAsync();
        AstraPlan.Read(stored[1].ResponseJson)!.Steps.Last().Status.Should().Be("pending");
        stored[1].ModelContextJson.Should().NotBeNull();
        return stored;
    }

    private sealed class RepairFailure(string stage, CancellationTokenSource? requestCancellation = null) : DbTransactionInterceptor
    {
        internal bool Enabled { get; set; }
        internal int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (stage == "start") Fail();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (stage != "start") Fail();
            return ValueTask.FromResult(result);
        }

        private void Fail()
        {
            if (!Enabled) return;
            Attempts++;
            if (requestCancellation is not null)
            {
                requestCancellation.Cancel();
                throw new OperationCanceledException(requestCancellation.Token);
            }
            if (stage == "database_cancellation") throw new OperationCanceledException("Database repair cancelled");
            throw new InvalidOperationException("Plan repair transaction failed");
        }
    }

    private sealed class RepairLogger : ILogger<ConversationTurnRecorder>
    {
        internal List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
