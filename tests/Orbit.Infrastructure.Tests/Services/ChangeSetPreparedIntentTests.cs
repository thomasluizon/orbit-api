using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ChangeSetPreparedIntentTests : IDisposable
{
    private readonly OrbitDbContext _db = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly PreparingCreateTool _create = new();
    private readonly RecordingTool _second = new("create_tag");
    private readonly PendingAgentOperationStore _store;
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly AgentCatalogService _catalog;
    private readonly AgentOperationExecutor _executor;
    private readonly User _user = User.Create("Alex", "alex@example.com").Value;

    public ChangeSetPreparedIntentTests()
    {
        _db.Users.Add(_user);
        _db.SaveChanges();
        var registry = new AiToolRegistry(new IAiTool[] { _create, _second });
        _catalog = new AgentCatalogService(registry.GetAll());
        _store = new PendingAgentOperationStore(_db, Options.Create(new AgentPlatformSettings()));
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        _previewer = new PendingOperationChangePreviewer(Substitute.For<IGenericRepository<Habit>>(),
            Substitute.For<IGenericRepository<Goal>>(), Substitute.For<IGenericRepository<Tag>>(), dates, registry);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _executor = new AgentOperationExecutor(_catalog,
            new AgentPolicyEvaluator(_db, _catalog, _store, bridge, new FeatureFlagService(_db, _cache,
                Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build())),
            Substitute.For<IAgentAuditService>(), ownership, bridge, registry, _previewer, _store,
            new UnitOfWork(_db, new DatabaseConnectionSettings()), NullLogger<AgentOperationExecutor>.Instance);
    }

    [Fact]
    public async Task SetMember_ExecutesItsStoredArgumentsWithoutPreparingAgain()
    {
        var creation = await HoldAsync(_create.Name, new { title = "Read" });
        var tag = await HoldAsync(_second.Name, new { title = "Health" });
        var stored = creation.Arguments.GetRawText();

        var approved = await ApproveSetAsync(creation, tag);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        _create.Preparations.Should().Be(1, "the prepared intent runs when the member is bound, not at approval");
        _create.Executed.Should().ContainSingle().Which.Should().Be(stored);
        _second.Executed.Should().ContainSingle().Which.Should().Be(tag.Arguments.GetRawText());
    }

    [Fact]
    public async Task SingleCreation_BindsItsPreparedIntentAndExecutesTheBoundArguments()
    {
        var creation = await HoldAsync(_create.Name, new { title = "Read" });

        _create.Preparations.Should().Be(1);
        creation.Arguments.GetProperty("prepared").GetInt32().Should().Be(1);
        var confirmation = _store.Confirm(_user.Id, creation.PendingOperationId)!;
        var approved = await _executor.ExecuteAsync(new(_user.Id, creation.OperationId, creation.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        _create.Executed.Should().ContainSingle().Which.Should().Be(creation.Arguments.GetRawText());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    private async Task<PendingAgentOperationExecution> HoldAsync(string operationId, object arguments)
    {
        var held = await _executor.ExecuteAsync(new(_user.Id, operationId, JsonSerializer.SerializeToElement(arguments),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, held.Operation.PolicyReason);
        return _store.GetExecution(_user.Id, held.PendingOperation!.Id)!;
    }

    private async Task<AgentExecuteOperationResponse> ApproveSetAsync(params PendingAgentOperationExecution[] members)
    {
        var arguments = AccountChangeSet.BuildArguments(members);
        var preview = (await _previewer.PreviewAsync(_user.Id, AccountChangeSet.OperationName, arguments)).Value!;
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, arguments.GetRawText());
        var pending = _store.Create(_user.Id, _catalog.GetCapability(AccountChangeSet.CapabilityId)!,
            AccountChangeSet.OperationName, arguments.GetRawText(), "Review", fingerprint, AgentExecutionSurface.Chat);
        _store.Revise(_user.Id, pending.Id, fingerprint, arguments.GetRawText(), fingerprint, preview.PreviewFingerprint!)
            .Should().BeTrue();
        var confirmation = _store.Confirm(_user.Id, pending.Id)!;
        return await _executor.ExecuteAsync(new(_user.Id, AccountChangeSet.OperationName, arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
    }

    /// <summary>A creation whose prepared intent differs on each run, so a second run is visible in what executes.</summary>
    private sealed class PreparingCreateTool : RecordingTool, IPreparedIntentTool
    {
        public PreparingCreateTool() : base("create_habit") { }

        public int Preparations { get; private set; }

        public Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            Preparations++;
            var node = JsonNode.Parse(args.GetRawText())!.AsObject();
            node["prepared"] = Preparations;
            return Task.FromResult<PreparedToolIntent?>(PreparedToolIntent.Create(Preparations, JsonSerializer.SerializeToElement(node)));
        }

        public Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
            Task.FromResult(Result.Success());
    }

    private class RecordingTool(string name) : IAiTool, ITransactionalAiTool
    {
        public string Name => name;
        public string Description => name;
        public List<string> Executed { get; } = [];

        public object GetParameterSchema() => new
        {
            type = "object",
            properties = new { title = new { type = "string" }, prepared = new { type = "integer" } }
        };

        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            Executed.Add(args.GetRawText());
            return Task.FromResult(new ToolResult(true, EntityId: Guid.NewGuid().ToString(), EntityName: name));
        }
    }
}
