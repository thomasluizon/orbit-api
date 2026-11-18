using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Profile.Commands;
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
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly Dictionary<string, CountingIntentTool> _prepared;
    private readonly PendingAgentOperationStore _store;
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly AgentCatalogService _catalog;
    private readonly AgentOperationExecutor _executor;
    private readonly User _user = User.Create("Alex", "alex@example.com").Value;
    private static readonly Guid NotificationId = Guid.Parse("7a8f1c1e-0d6b-4a53-9a51-5f0b2c1d0e11");

    public ChangeSetPreparedIntentTests()
    {
        _db.Users.Add(_user);
        _db.SaveChanges();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _mediator.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _mediator.Send(Arg.Any<SetLanguageCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _prepared = new IPreparedIntentTool[]
        {
            new UpdateProfilePreferencesTool(_mediator), new UpdateNotificationsTool(_mediator), new DeleteNotificationsTool(_mediator)
        }.ToDictionary(tool => tool.Name, tool => new CountingIntentTool(tool));
        var registry = new AiToolRegistry([_create, _second, .. _prepared.Values]);
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
    public async Task SetWithACreationANotificationAndAProfileMember_ExecutesEachStoredArgumentWithoutPreparingAgain()
    {
        var creation = await HoldAsync(_create.Name, new { title = "Read" });
        var notification = await HoldAsync("update_notifications", new { action = "mark_read", notification_id = NotificationId });
        var profile = await HoldAsync("update_profile_preferences", new { action = "set_language", language = "pt-BR" });

        var approved = await ApproveSetAsync(creation, notification, profile);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        _create.Preparations.Should().Be(1);
        _create.Executed.Should().ContainSingle().Which.Should().Be(creation.Arguments.GetRawText());
        foreach (var member in new[] { notification, profile })
        {
            _prepared[member.OperationId].Preparations.Should().Be(1, "the prepared intent runs when the member is bound, not at approval");
            _prepared[member.OperationId].Executed.Should().ContainSingle().Which.Should().Be(member.Arguments.GetRawText());
        }
    }

    public static TheoryData<string, string> PreparedKinds => new()
    {
        { "update_profile_preferences", JsonSerializer.Serialize(new { action = "set_language", language = "pt-BR", name = (string?)null }) },
        { "update_notifications", JsonSerializer.Serialize(new { action = "mark_read", notification_id = NotificationId, endpoint = (string?)null }) },
        { "delete_notifications", JsonSerializer.Serialize(new { action = "delete_selected", notification_ids = new[] { NotificationId } }) }
    };

    [Theory]
    [MemberData(nameof(PreparedKinds))]
    public async Task SetMemberOfAPreparedKind_ExecutesItsStoredArgumentsUnchanged(string kind, string arguments)
    {
        var member = await HoldAsync(kind, JsonDocument.Parse(arguments).RootElement);
        var tag = await HoldAsync(_second.Name, new { title = "Health" });

        var approved = await ApproveSetAsync(member, tag);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        _prepared[kind].Preparations.Should().Be(1, "the prepared intent runs when the member is bound, not at approval");
        _prepared[kind].Executed.Should().ContainSingle().Which.Should().Be(member.Arguments.GetRawText());
    }

    [Theory]
    [MemberData(nameof(PreparedKinds))]
    public async Task SingleBindOfAPreparedKind_RunsItsPreparedIntentAndExecutesTheBoundArguments(string kind, string arguments)
    {
        var held = await HoldAsync(kind, JsonDocument.Parse(arguments).RootElement);

        _prepared[kind].Preparations.Should().Be(1);
        held.Arguments.GetRawText().Should().NotContain("null", "the bound arguments are the prepared intent, with no optional null");
        var confirmation = _store.Confirm(_user.Id, held.PendingOperationId)!;
        var approved = await _executor.ExecuteAsync(new(_user.Id, held.OperationId, held.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        _prepared[kind].Executed.Should().ContainSingle().Which.Should().Be(held.Arguments.GetRawText());
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

    /// <summary>A real tool with a prepared intent. It counts each preparation and records what executes.</summary>
    private sealed class CountingIntentTool(IPreparedIntentTool inner) : IPreparedIntentTool, ITransactionalAiTool
    {
        public string Name => inner.Name;
        public string Description => inner.Description;
        public int Preparations { get; private set; }
        public List<string> Executed { get; } = [];

        public object GetParameterSchema() => inner.GetParameterSchema();

        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            Executed.Add(args.GetRawText());
            return inner.ExecuteAsync(args, userId, ct);
        }

        public Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
            inner.CheckArgumentsAsync(args, userId, ct);

        public Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            Preparations++;
            return inner.PrepareIntentAsync(args, userId, ct);
        }
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
