using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Result = Orbit.Domain.Common.Result;
using Orbit.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Authentication;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Mcp;

public class OAuthConnectorConfirmationTests : IDisposable
{
    private readonly OrbitDbContext _db = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly AgentCatalogService _catalog = new();
    private readonly PendingAgentOperationStore _store;
    private readonly AgentTools _tools;
    private readonly AgentPolicyEvaluator _policy;
    private readonly Guid _userId;

    public OAuthConnectorConfirmationTests()
    {
        var user = User.Create("Connector", "connector@example.com").Value;
        _userId = user.Id;
        _db.Users.Add(user);
        _db.SaveChanges();
        var settings = Options.Create(new AgentPlatformSettings());
        _store = new PendingAgentOperationStore(_db, settings);
        _tools = new AgentTools(_catalog, Substitute.For<IAgentOperationExecutor>(), _store,
            Substitute.For<IAgentStepUpService>());
        _policy = new AgentPolicyEvaluator(_db, _catalog, _store,
            Substitute.For<IAgentStepUpAuthorizationBridge>(), settings,
            new FeatureFlagService(_db, new MemoryCache(new MemoryCacheOptions())));
    }

    internal static async Task<ClaimsPrincipal> Authenticate(Guid userId, Guid? familyId,
        bool readOnly = false, IReadOnlyList<string>? scopes = null, bool legacy = false)
    {
        var (key, raw) = legacy
            ? LegacyConnectorCredentialMigrationTests.CreateLegacyCredential(userId, readOnly, scopes)
            : ApiKey.Create(userId, familyId.HasValue ? ApiKey.ConnectorName : "Manual key", scopes ?? [AgentScopes.DeleteHabits, AgentScopes.ReadHabits],
                isReadOnly: readOnly, oauthFamilyId: familyId).Value;
        if (legacy)
            key = LegacyConnectorCredentialMigrationTests.MigrateCredential(key);
        var run = await ApiKeyAuthenticationHandlerTests.RunHandler($"Bearer {raw}", candidates: [key]);
        run.Result.Succeeded.Should().BeTrue();
        return run.Result.Principal!;
    }

    internal static T Payload<T>(object? result)
    {
        var toolResult = result.Should().BeOfType<CallToolResult>().Subject;
        toolResult.IsError.Should().NotBe(true);
        return JsonSerializer.Deserialize<T>(toolResult.Content.Should().ContainSingle().Which
            .Should().BeOfType<TextContentBlock>().Which.Text, McpJsonUtilities.DefaultOptions)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_OnlyFamilyCredentialsAreOAuth(bool connector)
    {
        var user = await Authenticate(_userId, connector ? Guid.NewGuid() : null);
        user.GetAgentAuthMethod().ToString().Should().Be(connector ? "OAuth" : "ApiKey");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connector_DeleteConfirmRetry_DeletesHabitAndAuditsOAuth(bool legacy)
    {
        var user = await Authenticate(_userId, legacy ? null : Guid.NewGuid(), legacy: legacy);
        user.GetAgentAuthMethod().Should().Be(AgentAuthMethod.OAuth);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "QA habit", FrequencyUnit.Day, 1,
            new DateOnly(2026, 1, 1))).Value;
        _db.Habits.Add(habit);
        await _db.SaveChangesAsync();
        var repository = new GenericRepository<Habit>(_db);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 1, 1));
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => _db.SaveChangesAsync(call.Arg<CancellationToken>()));
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result>>>(0)(call.ArgAt<CancellationToken>(1)));
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<AgentExecuteOperationResponse>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<AgentExecuteOperationResponse>>>(0)(call.ArgAt<CancellationToken>(1)));
        var mediator = Substitute.For<IMediator>();
        var handler = new DeleteHabitCommandHandler(repository, Substitute.For<IUserStreakService>(), unitOfWork,
            dates, new MemoryCache(new MemoryCacheOptions()));
        mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<DeleteHabitCommand>(), call.Arg<CancellationToken>()));
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var readHandler = new GetHabitByIdQueryHandler(repository, new GenericRepository<HabitLog>(_db), dates);
        mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => readHandler.Handle(call.Arg<GetHabitByIdQuery>(), call.Arg<CancellationToken>()));
        var tool = new DeleteHabitTool(mediator, repository);
        var catalog = new AgentCatalogService([tool]);
        var audit = Substitute.For<IAgentAuditService>();
        var ownership = new AgentTargetOwnershipService(_db);
        var executor = new AgentOperationExecutor(catalog, _policy, audit, ownership,
            Substitute.For<IAgentStepUpAuthorizationBridge>(), new AiToolRegistry([tool]),
            Substitute.For<IPendingOperationChangePreviewer>(), _store, unitOfWork,
            NullLogger<AgentOperationExecutor>.Instance);
        var tools = new HabitTools(mediator, dates, new McpExecutorBridge(executor));

        (await tools.GetHabit(user, habit.Id.ToString())).Should().Contain("QA habit");
        (await tools.DeleteHabit(user, habit.Id.ToString())).Should().Contain("confirm_agent_operation_v2");
        habit.IsDeleted.Should().BeFalse();
        var id = _db.PendingAgentOperations.Single().Id;
        var confirmation = Payload<PendingAgentOperationConfirmation>(_tools.ConfirmAgentOperation(user, id.ToString()));
        (await tools.DeleteHabit(user, habit.Id.ToString(), confirmation.ConfirmationToken))
            .Should().Be($"Deleted habit {habit.Id}");
        habit.IsDeleted.Should().BeTrue();
        (await tools.GetHabit(user, habit.Id.ToString())).Should().StartWith("Error:");
        await audit.Received().RecordAsync(Arg.Is<AgentAuditEntry>(entry =>
            entry.AuthMethod.ToString() == "OAuth" && entry.OutcomeStatus == AgentOperationStatus.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectorConfirmation_IsBoundToUserOperationArgumentsAndSingleUse(bool legacy)
    {
        var user = await Authenticate(_userId, legacy ? null : Guid.NewGuid(), legacy: legacy);
        user.GetAgentAuthMethod().Should().Be(AgentAuthMethod.OAuth);
        var context = Context(user, "delete_habit:original");
        var decision = _policy.Evaluate(context);
        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        var pending = decision.PendingOperation!;
        var confirmation = Payload<PendingAgentOperationConfirmation>(
            _tools.ConfirmAgentOperation(user, pending.Id.ToString()));

        _policy.Evaluate(context with { UserId = Guid.NewGuid(), ConfirmationToken = confirmation.ConfirmationToken })
            .Status.Should().Be(AgentPolicyDecisionStatus.Denied);
        _store.Confirm(Guid.NewGuid(), pending.Id).Should().BeNull();
        _store.TryConsumeFreshConfirmation(Guid.NewGuid(), AgentCapabilityIds.HabitsDelete,
            context.OperationFingerprint!, confirmation.ConfirmationToken, false).Should().BeFalse();
        _store.TryConsumeFreshConfirmation(_userId, AgentCapabilityIds.GoalsDelete,
            context.OperationFingerprint!, confirmation.ConfirmationToken, false).Should().BeFalse();
        _policy.Evaluate(context with { OperationFingerprint = "delete_habit:other", ConfirmationToken = confirmation.ConfirmationToken })
            .Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        _policy.Evaluate(context with { ConfirmationToken = confirmation.ConfirmationToken })
            .Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        _policy.Evaluate(context with { ConfirmationToken = confirmation.ConfirmationToken })
            .Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
    }

    [Fact]
    public async Task RefreshedCredential_ConfirmsPendingOperationFromSameUserAndFamily()
    {
        var familyId = Guid.NewGuid();
        var original = await Authenticate(_userId, familyId);
        var context = Context(original, "delete_habit:refresh");
        var decision = _policy.Evaluate(context);
        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        var pending = decision.PendingOperation!;
        var refreshed = await Authenticate(_userId, familyId);
        refreshed.FindFirst("api_key_id")!.Value.Should().NotBe(original.FindFirst("api_key_id")!.Value);

        var confirmation = Payload<PendingAgentOperationConfirmation>(
            _tools.ConfirmAgentOperation(refreshed, pending.Id.ToString()));

        _policy.Evaluate(Context(refreshed, context.OperationFingerprint!) with
        { ConfirmationToken = confirmation.ConfirmationToken }).Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
    }

    [Fact]
    public async Task LegacyAndNewFamilyCredentials_CanConfirmTheirUsersPendingOperation()
    {
        var legacy = await Authenticate(_userId, null, legacy: true);
        var newer = await Authenticate(_userId, Guid.NewGuid());
        foreach (var user in new[] { legacy, newer })
        {
            var context = Context(user, $"delete_habit:{user.FindFirst("api_key_id")!.Value}");
            var pending = _policy.Evaluate(context).PendingOperation!;
            var confirmation = Payload<PendingAgentOperationConfirmation>(
                _tools.ConfirmAgentOperation(user, pending.Id.ToString()));

            _policy.Evaluate(context with { ConfirmationToken = confirmation.ConfirmationToken })
                .Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        }
    }

    [Fact]
    public async Task UnverifiedStoredKeyWithConnectorName_KeepsManualKeyRefusal()
    {
        var (key, raw) = LegacyConnectorCredentialMigrationTests.CreateLegacyCredential(_userId);
        typeof(ApiKey).GetProperty(nameof(ApiKey.Id))!.SetValue(key, Guid.NewGuid());
        key = LegacyConnectorCredentialMigrationTests.MigrateCredential(key);
        var run = await ApiKeyAuthenticationHandlerTests.RunHandler($"Bearer {raw}", candidates: [key]);

        run.Result.Succeeded.Should().BeTrue();
        var user = run.Result.Principal!;
        user.GetAgentAuthMethod().Should().Be(AgentAuthMethod.ApiKey);
        AssertError(_tools.ConfirmAgentOperation(user, Guid.NewGuid().ToString()), "interactive_confirmation_required");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignConnector_CannotConfirmAnotherUsersPendingOperation(bool legacy)
    {
        var user = await Authenticate(_userId, legacy ? null : Guid.NewGuid(), legacy: legacy);
        user.GetAgentAuthMethod().Should().Be(AgentAuthMethod.OAuth);
        var pending = _policy.Evaluate(Context(user, "delete_habit:owner")).PendingOperation!;
        var foreign = await Authenticate(Guid.NewGuid(), Guid.NewGuid());
        var result = _tools.ConfirmAgentOperation(foreign, pending.Id.ToString());
        result.Should().BeOfType<CallToolResult>().Which.IsError.Should().BeTrue();
        _db.PendingAgentOperations.Single().ConfirmedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("issue")]
    [InlineData("verify")]
    public async Task ManualKey_ApprovalToolsReturnTypedRefusalWithoutThrowing(string action)
    {
        var user = await Authenticate(_userId, null);
        var id = Guid.NewGuid().ToString();
        object? result = action switch
        {
            "confirm" => _tools.ConfirmAgentOperation(user, id),
            "issue" => await _tools.StepUpAgentOperation(user, id),
            _ => await _tools.VerifyStepUpAgentOperation(user, id, id, "123456")
        };
        AssertError(result, "interactive_confirmation_required");
    }

    [Theory]
    [InlineData("confirm", false)]
    [InlineData("issue", false)]
    [InlineData("verify", false)]
    [InlineData("confirm", true)]
    [InlineData("issue", true)]
    [InlineData("verify", true)]
    public async Task ReadOnlyConnector_CannotApproveAnyWrite(string action, bool legacy)
    {
        var writable = await Authenticate(_userId, Guid.NewGuid());
        var pending = _policy.Evaluate(Context(writable, "delete_habit:read-only")).PendingOperation!;
        var user = await Authenticate(_userId, legacy ? null : Guid.NewGuid(), readOnly: true, legacy: legacy);
        user.GetAgentAuthMethod().Should().Be(AgentAuthMethod.OAuth);
        var id = pending.Id.ToString();
        object? result = action switch
        {
            "confirm" => _tools.ConfirmAgentOperation(user, id),
            "issue" => await _tools.StepUpAgentOperation(user, id),
            _ => await _tools.VerifyStepUpAgentOperation(user, id, Guid.NewGuid().ToString(), "123456")
        };
        AssertError(result, "read_only_credential");
        _db.PendingAgentOperations.Single().ConfirmedAtUtc.Should().BeNull();
        _policy.Evaluate(Context(user, "delete_habit:read-only")).Reason.Should().Be("read_only_credential");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidOAuthCredential_CannotAuthenticateToConfirm(bool revoked, bool legacy)
    {
        var (key, raw) = legacy
            ? LegacyConnectorCredentialMigrationTests.CreateLegacyCredential(_userId)
            : ApiKey.Create(_userId, "Claude.ai", oauthFamilyId: Guid.NewGuid()).Value;
        if (revoked)
            key.Revoke();
        else
            typeof(ApiKey).GetProperty(nameof(ApiKey.ExpiresAtUtc))!.SetValue(key, DateTime.UtcNow.AddMinutes(-1));
        if (legacy)
            key = LegacyConnectorCredentialMigrationTests.MigrateCredential(key);
        var run = await ApiKeyAuthenticationHandlerTests.RunHandler($"Bearer {raw}", candidates: [key]);
        run.Result.Succeeded.Should().BeFalse();
        run.Result.Principal.Should().BeNull();
    }

    [Fact]
    public async Task ManualKey_BridgeExplainsInteractiveRecoveryPath()
    {
        var user = await Authenticate(_userId, null);
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult("delete_habit", "delete_habit",
                AgentRiskClass.High, AgentConfirmationRequirement.FreshConfirmation,
                AgentOperationStatus.PendingConfirmation, PolicyReason: "confirmation_required", PendingOperationId: Guid.NewGuid())));

        var result = await new McpExecutorBridge(executor).ExecuteAsync(user, "delete_habit", new { }, null, default);

        result.Message.Should().Contain("Claude connector").And.Contain("app").And.NotContain("via confirm_agent_operation_v2");
    }

    private AgentPolicyEvaluationContext Context(ClaimsPrincipal user, string fingerprint) => new(
        AgentCapabilityIds.HabitsDelete, user.GetUserId(), AgentExecutionSurface.Mcp,
        user.GetAgentAuthMethod(), user.GetGrantedAgentScopes(), "delete_habit", "Delete habit",
        OperationFingerprint: fingerprint, IsReadOnlyCredential: user.IsReadOnlyCredential());

    internal static void AssertError(object? result, string code)
    {
        var error = result.Should().BeOfType<CallToolResult>().Subject;
        error.IsError.Should().BeTrue();
        JsonSerializer.SerializeToElement(error, McpJsonUtilities.DefaultOptions)
            .GetProperty("isError").GetBoolean().Should().BeTrue();
        if (code == "interactive_confirmation_required")
            error.StructuredContent!.Value.GetProperty("message").GetString()
                .Should().Contain("Claude connector").And.Contain("app");
        error.StructuredContent!.Value.GetProperty("code").GetString().Should().Be(code);
        error.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text
            .Should().Contain(code);
    }

    public void Dispose() => _db.Dispose();
}
