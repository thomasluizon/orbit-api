using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class PlanCommittedOutcomeTests
{
    [Fact]
    public async Task Execute_AwaitsPolicyBeforeCommittingAndRepairingPlan()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var confirmation = fixture.Pending.Confirm(fixture.UserId, id)!;
        var policyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PausePolicyUntil(policyRelease.Task);
        fixture.FaultPlans("done", id);

        var executing = fixture.Executor.ExecuteAsync(new(fixture.UserId, "create_goal", execution.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        executing.IsCompleted.Should().BeFalse();
        fixture.Db.Goals.Should().BeEmpty();
        fixture.Db.AgentAuditLogs.Should().BeEmpty();
        fixture.Pending.GetConfirmedExecution(fixture.UserId, execution.CapabilityId,
            AgentOperationFingerprint.Compute("create_goal", execution.Arguments.GetRawText()), confirmation.ConfirmationToken)
            .Should().NotBeNull();

        policyRelease.SetResult();
        var response = await executing;

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        fixture.AssertError(id);
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Goals.SingleAsync()).Title.Should().Be("Read books");
        var outcome = await fixture.Db.AgentAuditLogs.SingleAsync(entry =>
            entry.SourceName == ConversationPlanUpdates.ExecutionAuditSource && entry.TargetId == id.ToString());
        outcome.OutcomeStatus.Should().Be(AgentOperationStatus.Succeeded);
        fixture.Pending.GetExecution(fixture.UserId, id).Should().BeNull();
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be("done");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_PlanFailurePreservesCommittedGoalAndConsumedConfirmation(bool withPlan)
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(recordPlan: withPlan);
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var confirmation = fixture.Pending.Confirm(fixture.UserId, id)!;
        fixture.FaultPlans("done", id);

        var response = await fixture.Executor.ExecuteAsync(new(fixture.UserId, "create_goal", execution.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(response));
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Goals.SingleAsync()).Title.Should().Be("Read books");
        fixture.Pending.GetExecution(fixture.UserId, id).Should().BeNull();
        fixture.Pending.TryConsumeFreshConfirmation(fixture.UserId, execution.CapabilityId,
            AgentOperationFingerprint.Compute("create_goal", execution.Arguments.GetRawText()), confirmation.ConfirmationToken, false).Should().BeFalse();
        fixture.AssertError(id);
        if (!withPlan) return;
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be("done");
        fixture.Db.ChangeTracker.Clear();
        AstraPlan.Read((await fixture.Db.ConversationTurns.SingleAsync(row => row.Role == "assistant")).ResponseJson)!
            .Steps.Last().Status.Should().Be("done");
    }

    [Fact]
    public async Task Confirm_PlanFailureReturnsUsablePersistedConfirmation()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        fixture.FaultPlans("running", id);
        var controller = fixture.Controller();

        var response = (await controller.ConfirmPendingOperation(id, default)).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AiController.ConfirmPendingOperationResponse>().Subject;

        fixture.Db.ChangeTracker.Clear();
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        fixture.Pending.GetConfirmedExecution(fixture.UserId, execution.CapabilityId,
            AgentOperationFingerprint.Compute("create_goal", execution.Arguments.GetRawText()), response.ConfirmationToken).Should().NotBeNull();
        fixture.Db.Goals.Should().BeEmpty();
        fixture.AssertError(id);
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be("running");
        var executed = await fixture.Executor.ExecuteAsync(new(fixture.UserId, "create_goal", execution.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: response.ConfirmationToken));
        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        fixture.Pending.GetExecution(fixture.UserId, id).Should().BeNull();
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Goals.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_PlanFailurePreservesRejectionAndReload(bool changeSet)
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(changeSet);
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        fixture.FaultPlans("skipped", id);

        var response = await fixture.Revisions.ReviseAsync(fixture.UserId, id, new(execution.PreviewFingerprint!, []), default);

        response.IsSuccess.Should().BeTrue();
        response.Cancelled.Should().BeTrue();
        fixture.Db.ChangeTracker.Clear();
        fixture.Pending.GetExecution(fixture.UserId, id).Should().BeNull();
        fixture.Pending.Confirm(fixture.UserId, id).Should().BeNull();
        fixture.Db.Goals.Should().BeEmpty();
        fixture.AssertError(id);
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be("skipped");
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("confirm")]
    [InlineData("cancel")]
    [InlineData("cancel_set")]
    public async Task PlanUpdate_RequestCancellationStillPropagates(string action)
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(action == "cancel_set");
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var confirmation = action == "execute" ? fixture.Pending.Confirm(fixture.UserId, id) : null;
        using var cancellation = new CancellationTokenSource();
        fixture.CancelDuringPlanUpdate(id, cancellation);
        Func<Task> act = action switch
        {
            "execute" => async () => await fixture.Executor.ExecuteAsync(new(fixture.UserId, "create_goal", execution.Arguments,
                AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation!.ConfirmationToken), cancellation.Token),
            "confirm" => async () => await fixture.Controller().ConfirmPendingOperation(id, cancellation.Token),
            _ => async () => await fixture.Revisions.ReviseAsync(fixture.UserId, id, new(execution.PreviewFingerprint!, []), cancellation.Token)
        };
        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Goals.CountAsync()).Should().Be(action == "execute" ? 1 : 0);
        if (action != "confirm") fixture.Pending.GetExecution(fixture.UserId, id).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_ConsumedConfirmationWithoutExecutionOutcomeRemainsRunning(bool expired)
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var confirmation = fixture.Pending.Confirm(fixture.UserId, id)!;
        fixture.Pending.TryConsumeFreshConfirmation(fixture.UserId, execution.CapabilityId,
            AgentOperationFingerprint.Compute("create_goal", execution.Arguments.GetRawText()), confirmation.ConfirmationToken, false).Should().BeTrue();
        if (expired)
        {
            var state = await fixture.Db.PendingAgentOperations.SingleAsync(item => item.Id == id);
            fixture.Db.Entry(state).Property(item => item.ExpiresAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
            await fixture.Db.SaveChangesAsync();
        }
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be("running");
    }

    [Fact]
    public async Task Reload_FailedToolRepairsFailedRatherThanDone()
    {
        using var fixture = new Fixture(failGoal: true);
        var id = await fixture.ProposeAsync();
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var confirmation = fixture.Pending.Confirm(fixture.UserId, id)!;
        fixture.FaultPlans("failed", id);
        var response = await fixture.Executor.ExecuteAsync(new(fixture.UserId, "create_goal", execution.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        fixture.AssertError(id);
        fixture.Db.ChangeTracker.Clear();
        fixture.Db.Goals.Should().BeEmpty();
        fixture.Pending.GetExecution(fixture.UserId, id).Should().BeNull();
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be("failed");
    }

    [Theory]
    [InlineData(false, "running")]
    [InlineData(true, "skipped")]
    public async Task Reload_RepairsFromPendingStateOnlyWhileRecordingEnabled(bool reject, string status)
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        fixture.SetRecording(false);
        if (reject) fixture.Pending.Cancel(fixture.UserId, id,
            AgentOperationFingerprint.Compute("create_goal", execution.Arguments.GetRawText())).Should().BeTrue();
        else fixture.Pending.Confirm(fixture.UserId, id).Should().NotBeNull();
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be("pending");
        fixture.SetRecording(true);
        (await fixture.ReloadAsync()).Steps.Last().Status.Should().Be(status);
        fixture.Db.ChangeTracker.Clear();
        AstraPlan.Read((await fixture.Db.ConversationTurns.SingleAsync(row => row.Role == "assistant")).ResponseJson)!
            .Steps.Last().Status.Should().Be(status);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ConversationTurnRecorderTests.Fixture _conversation = new();
        private readonly AiToolRegistry _tools;
        private readonly AgentCatalogService _catalog;
        private readonly IPendingOperationChangePreviewer _previewer;
        private readonly IAgentAuditService _audit = Substitute.For<IAgentAuditService>();
        private readonly IConversationPlanStore _plans = Substitute.For<IConversationPlanStore>();
        private readonly IAgentPolicyEvaluator _policy = Substitute.For<IAgentPolicyEvaluator>();
        private readonly List<(LogLevel Level, string Text)> _logs = [];
        internal OrbitDbContext Db => _conversation.Db;
        internal Guid UserId => _conversation.User.Id;
        internal PendingAgentOperationStore Pending { get; }
        internal AgentOperationExecutor Executor { get; }
        internal PendingOperationRevisionService Revisions { get; }

        internal Fixture(bool failGoal = false)
        {
            var unit = new UnitOfWork(Db, new DatabaseConnectionSettings());
            var tool = new CreateGoalTool(Orbit.Tests.Shared.GoalToolCommandMediator.Create(new GenericRepository<Goal>(Db), unit,
                habits: new GenericRepository<Habit>(Db), configureTransactions: false));
            IAiTool registered = failGoal ? new FailingGoalTool(tool) : tool;
            _tools = new([registered]);
            _catalog = new([registered]);
            Pending = new(Db, Options.Create(new AgentPlatformSettings()), _conversation.Flags);
            _previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(Db), new GenericRepository<Goal>(Db),
                new GenericRepository<Tag>(Db), Substitute.For<IUserDateService>(), _tools);
            _policy.EvaluateAsync(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
                EvaluatePolicy(call.Arg<AgentPolicyEvaluationContext>()));
            _audit.RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                using var db = _conversation.Database.CreateContext();
                db.AgentAuditLogs.Add(AgentAuditLog.Create(call.Arg<AgentAuditEntry>()));
                await db.SaveChangesAsync(call.Arg<CancellationToken>());
            });
            var ownership = Substitute.For<IAgentTargetOwnershipService>();
            ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
            Executor = new(_catalog, _policy, _audit, ownership,
                Substitute.For<IAgentStepUpAuthorizationBridge>(), _tools, _previewer, Pending, unit,
                new Logger<AgentOperationExecutor>(_logs), plans: _plans);
            Revisions = new(Pending, _previewer, new RevisePendingOperationRequestValidator(), _tools, plans: _plans, logger: new Logger<PendingOperationRevisionService>(_logs));
        }

        private AgentPolicyDecision EvaluatePolicy(AgentPolicyEvaluationContext context)
        {
            var capability = _catalog.GetCapabilityByChatTool("create_goal")!;
            return Pending.TryConsumeFreshConfirmation(UserId, capability.Id,
                context.OperationFingerprint!, context.ConfirmationToken!, false)
                ? new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability, ConsumedConfirmation: true)
                : new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability);
        }

        internal void PausePolicyUntil(Task release) =>
            _policy.EvaluateAsync(Arg.Any<AgentPolicyEvaluationContext>()).Returns(async call =>
            {
                await release;
                return EvaluatePolicy(call.Arg<AgentPolicyEvaluationContext>());
            });

        internal async Task<Guid> ProposeAsync(bool changeSet = false, bool recordPlan = true)
        {
            var capture = _conversation.Capture();
            var arguments = JsonSerializer.SerializeToElement(new { title = "Read books" });
            var capability = _catalog.GetCapabilityByChatTool("create_goal")!;
            var fingerprint = AgentOperationFingerprint.Compute("create_goal", arguments.GetRawText());
            var proposed = Pending.Create(UserId, capability, "create_goal", arguments.GetRawText(), "Reading", fingerprint,
                AgentExecutionSurface.Chat, new(_conversation.ConversationId, capture.CreatedAtUtc));
            var preview = (await _previewer.PreviewAsync(UserId, "create_goal", arguments)).Value!;
            Pending.Revise(UserId, proposed.Id, fingerprint, arguments.GetRawText(), fingerprint, preview.PreviewFingerprint!).Should().BeTrue();
            var id = proposed.Id;
            if (changeSet)
            {
                arguments = AccountChangeSet.BuildArguments([Pending.GetExecution(UserId, id)!]);
                preview = (await _previewer.PreviewAsync(UserId, AccountChangeSet.OperationName, arguments)).Value!;
                arguments = preview.ExecutionArguments!.Value;
                fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, arguments.GetRawText());
                id = Pending.Create(UserId, _catalog.GetCapability(AccountChangeSet.CapabilityId)!, AccountChangeSet.OperationName,
                    arguments.GetRawText(), "Reading", fingerprint, AgentExecutionSurface.Chat,
                    new(_conversation.ConversationId, capture.CreatedAtUtc)).Id;
                Pending.Revise(UserId, id, fingerprint, arguments.GetRawText(), fingerprint, preview.PreviewFingerprint!).Should().BeTrue();
            }
            var plan = AstraPlan.Create("Reading", [new("1", "Prepare a preview", "done", "create_goal", "execution"),
                new("2", "Save your goal", "pending", "create_goal", "approval", PendingOperationId: id)]).Value;
            await _conversation.Store.RecordAsync(capture with
            { ResponseJson = JsonSerializer.Serialize(new ChatResponse(null, [], Plan: recordPlan ? plan : null), AstraPlan.JsonOptions) });
            return id;
        }

        internal AiController Controller()
        {
            var controller = new AiController(_catalog, Substitute.For<IAgentPolicyEvaluator>(),
                new(Pending, Substitute.For<IPendingClarificationStore>()), Substitute.For<IAgentStepUpService>(), _audit,
                Executor, Revisions, Substitute.For<IValidator<ResolveClarificationRequest>>(),
                new Logger<AiController>(_logs), _plans);
            controller.ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Test")) }
            };
            return controller;
        }

        internal void FaultPlans(string status, Guid id) => _plans.ChangeAsync(UserId, id, status, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AstraPlan?>(new InvalidOperationException("plan storage unavailable")));
        internal void CancelDuringPlanUpdate(Guid id, CancellationTokenSource cancellation) =>
            _plans.ChangeAsync(UserId, id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<AstraPlan?>(cancellation.Token);
            });
        internal void SetRecording(bool enabled)
        {
            _conversation.Flags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>(), Arg.Any<bool>())
                .Returns(enabled ? [Orbit.Application.Common.FeatureFlagKeys.AstraConversationRecording] : []);
            _conversation.Flags.ResolveForUser(UserId).Returns([new(Orbit.Application.Common.FeatureFlagKeys.AstraConversationRecording, enabled, null)]);
        }
        internal void AssertError(Guid id) => _logs.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Text.Contains(id.ToString()));
        internal async Task<AstraPlan> ReloadAsync() => (await _conversation.Store.GetLatestAsync(UserId,
            _conversation.ConversationId, null, 10))!.Turns.Single(turn => turn.Role == "assistant").Plan!;
        public void Dispose() => _conversation.Dispose();
    }

    private sealed class FailingGoalTool(CreateGoalTool inner) : IAiTool, IArgumentCheckTool
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public object GetParameterSchema() => inner.GetParameterSchema();
        public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
            inner.CheckArgumentsAsync(args, userId, ct);
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
            Task.FromResult(new ToolResult(false, Error: "write rejected"));
    }

    private sealed class Logger<T>(List<(LogLevel Level, string Text)> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add((logLevel, formatter(state, exception)));
    }
}
