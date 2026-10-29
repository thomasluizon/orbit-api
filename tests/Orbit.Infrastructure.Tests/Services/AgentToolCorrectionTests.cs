using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class AgentToolCorrectionTests : IDisposable
{
    private readonly OrbitDbContext _db;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAgentTargetOwnershipService _ownership = Substitute.For<IAgentTargetOwnershipService>();
    private readonly List<Habit> _saved = [];
    private readonly PendingAgentOperationStore _store;
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly AiToolRegistry _registry;
    private readonly AgentOperationExecutor _executor;
    private readonly Guid _userId;
    private const string RejectedArguments = """{"title":"Read","frequency_unit":"Day","due_time":"21:00:00"}""";
    private const string CorrectedArguments = """{"title":"Read","frequency_unit":"Day","due_time":"21:00"}""";

    public AgentToolCorrectionTests()
    {
        _db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"AgentToolCorrection_{Guid.NewGuid()}").Options);
        var user = User.Create("Alex", "alex@example.com").Value;
        user.GrantLifetimePro();
        _userId = user.Id;
        _db.Users.Add(user);
        _db.SaveChanges();
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var tags = Substitute.For<IGenericRepository<Tag>>();
        var goals = Substitute.For<IGenericRepository<Goal>>();
        habits.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _saved.Add(call.ArgAt<Habit>(0));
            return Task.CompletedTask;
        });
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 1));
        var gate = Substitute.For<IPayGateService>();
        gate.CanCreateHabits(_userId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<ToolResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<ToolResult>>>(0)(call.ArgAt<CancellationToken>(1)));
        var tool = new CreateHabitTool(dates, Orbit.Tests.Shared.HabitToolCommandMediator.Create(habits, tags, goals, dates, gate, _unitOfWork));
        _registry = new AiToolRegistry([tool]);
        var catalog = new AgentCatalogService([tool]);
        var settings = Options.Create(new AgentPlatformSettings { ShadowModeEnabled = false });
        _store = new PendingAgentOperationStore(_db, settings);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentConfirmationRequirement?)null);
        var policy = new AgentPolicyEvaluator(_db, catalog, _store, bridge, settings, new FeatureFlagService(_db, _cache, Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build()));
        _ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _previewer = new PendingOperationChangePreviewer(habits, goals, tags, dates, _registry);
        _executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(), _ownership, bridge,
            _registry, _previewer, _store, _unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
    }

    [Fact]
    public async Task RejectionEvidencePrecedesPolicyReservationsAndAllWritesAndStaysOffTheWire()
    {
        var response = await _executor.ExecuteAsync(Request(RejectedArguments));

        response.Should().BeOfType<RejectedBeforeDispatchResponse>();
        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _saved.Should().BeEmpty();
        _db.PendingAgentOperations.Should().BeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        var wire = JsonSerializer.SerializeToElement(response, response.GetType());
        wire.EnumerateObject().Select(field => field.Name).Should().BeEquivalentTo("Operation", "PendingOperation", "PolicyDenial");
        wire.GetRawText().Should().NotContain("21:00:00");
    }

    [Fact]
    public async Task CorrectedCallUsesTheOrdinaryApprovalBindingAndCannotConsumeItTwice()
    {
        (await _executor.ExecuteAsync(Request(RejectedArguments))).Should().BeOfType<RejectedBeforeDispatchResponse>();
        var proposal = await _executor.ExecuteAsync(Request(CorrectedArguments));

        proposal.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        proposal.PendingOperation!.Items.Should().ContainSingle();
        _saved.Should().BeEmpty();
        var pending = proposal.PendingOperation;
        _store.Confirm(Guid.NewGuid(), pending.Id).Should().BeNull();
        var stored = _store.GetExecution(_userId, pending.Id)!;
        var approval = _store.Confirm(_userId, pending.Id)!;
        var request = Request(stored.Arguments.GetRawText()) with { ConfirmationToken = approval.ConfirmationToken };
        var executed = await _executor.ExecuteAsync(request);
        var repeated = await _executor.ExecuteAsync(request);

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        repeated.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        _saved.Should().ContainSingle();
        _saved[0].Title.Should().Be("Read");
        _saved[0].FrequencyUnit.Should().Be(FrequencyUnit.Day);
        _saved[0].DueTime.Should().Be(new TimeOnly(21, 0));
        _store.GetExecution(_userId, pending.Id).Should().BeNull();
    }

    [Fact]
    public async Task CorrectedProposalRetainsTheRevisionOwnersApprovalInvalidation()
    {
        (await _executor.ExecuteAsync(Request(RejectedArguments))).Should().BeOfType<RejectedBeforeDispatchResponse>();
        var proposal = await _executor.ExecuteAsync(Request(CorrectedArguments));
        var pending = proposal.PendingOperation!;
        var original = _store.GetExecution(_userId, pending.Id)!;
        var oldApproval = _store.Confirm(_userId, pending.Id)!;
        var revisions = new PendingOperationRevisionService(_store, _previewer,
            new RevisePendingOperationRequestValidator(), _registry);
        var revised = await revisions.ReviseAsync(_userId, pending.Id, new RevisePendingOperationRequest(
            pending.PreviewFingerprint!, [new RevisedPendingOperationItem(pending.Items![0].ItemId,
                Parse("""{"due_time":"22:00"}"""))]), default);

        revised.IsSuccess.Should().BeTrue();
        var stale = await _executor.ExecuteAsync(Request(original.Arguments.GetRawText()) with
        {
            ConfirmationToken = oldApproval.ConfirmationToken
        });
        stale.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        _saved.Should().BeEmpty();
        var current = _store.GetExecution(_userId, pending.Id)!;
        current.Arguments.GetProperty("due_time").GetString().Should().Be("22:00");
        var approval = _store.Confirm(_userId, pending.Id)!;
        var executed = await _executor.ExecuteAsync(Request(current.Arguments.GetRawText()) with
        {
            ConfirmationToken = approval.ConfirmationToken
        });

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _saved.Should().ContainSingle(habit => habit.DueTime == new TimeOnly(22, 0));
    }

    [Fact]
    public async Task OwnershipDenialIsNeverARecoverableShapeRejection()
    {
        _ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns("target_not_owned");

        var result = await _executor.ExecuteAsync(Request(RejectedArguments));

        result.Should().BeOfType<AgentExecuteOperationResponse>();
        result.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        result.Operation.PolicyReason.Should().Be("target_not_owned");
        _saved.Should().BeEmpty();
        _db.PendingAgentOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task McpRejectionCannotMintAnInProcessChatCorrection()
    {
        var result = await _executor.ExecuteAsync(Request(RejectedArguments) with { Surface = AgentExecutionSurface.Mcp });

        result.Should().BeOfType<AgentExecuteOperationResponse>();
        result.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _saved.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingIntentInformationCannotProduceARejectionCertificate()
    {
        var result = await _executor.ExecuteAsync(Request("""{"title":"Read","frequency_unit":"Day","due_time":"tomorrow night"}"""));

        result.Should().BeOfType<AgentExecuteOperationResponse>();
        result.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pending = result.PendingOperation!;
        var stored = _store.GetExecution(_userId, pending.Id)!;
        var approval = _store.Confirm(_userId, pending.Id)!;
        var rejected = await _executor.ExecuteAsync(Request(stored.Arguments.GetRawText()) with
        {
            ConfirmationToken = approval.ConfirmationToken
        });
        rejected.Should().BeOfType<AgentExecuteOperationResponse>();
        rejected.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _saved.Should().BeEmpty();
    }

    private AgentExecuteOperationRequest Request(string arguments) =>
        new(_userId, "create_habit", Parse(arguments), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt);

    private static JsonElement Parse(string arguments) => JsonSerializer.Deserialize<JsonElement>(arguments);

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }
}
