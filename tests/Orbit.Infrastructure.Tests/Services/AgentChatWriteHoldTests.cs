using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AgentChatWriteHoldTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly MemoryCache _flagCache = new(new MemoryCacheOptions());
    private readonly AgentCatalogService _catalogService = BuildCatalog();
    private readonly PendingAgentOperationStore _pendingOperationStore;
    private readonly AgentPolicyEvaluator _policyEvaluator;
    private readonly Guid _userId;

    public AgentChatWriteHoldTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"AgentChatWriteHoldTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        var user = User.Create("Alex", "alex@test.com").Value;
        user.GrantLifetimePro();
        _userId = user.Id;
        _dbContext.Users.Add(user);

        foreach (var key in _catalogService.GetCapabilities()
            .SelectMany(capability => capability.FeatureFlagKeys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create(key, true, null, key));
        }

        _dbContext.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        _pendingOperationStore = new PendingAgentOperationStore(_dbContext, settings);
        var stepUpBridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUpBridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        _policyEvaluator = new AgentPolicyEvaluator(
            _dbContext, _catalogService, _pendingOperationStore, stepUpBridge, settings,
            new FeatureFlagService(_dbContext, _flagCache));
    }

    public void Dispose()
    {
        _flagCache.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static AgentCatalogService BuildCatalog() => new(ProductionTools());

    private static IReadOnlyList<IAiTool> ProductionTools()
    {
        return typeof(IAiTool).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(IAiTool).IsAssignableFrom(type) && !typeof(IChatOnlyAiTool).IsAssignableFrom(type))
            .Select(type => (IAiTool)RuntimeHelpers.GetUninitializedObject(type))
            .Select(tool => (IAiTool)new StubTool(tool.Name, tool.IsReadOnly))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static TheoryData<string> ChatWriteOperations()
    {
        var data = new TheoryData<string>();
        foreach (var operation in BuildCatalog().GetOperations()
            .Where(operation => operation.IsMutation && operation.IsAgentExecutable)
            .Where(operation => !AgentChatWriteHold.ExemptOperationIds.Contains(operation.Id))
            .OrderBy(operation => operation.Id, StringComparer.Ordinal))
        {
            data.Add(operation.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ChatWriteOperations))]
    public async Task ExecuteAsync_EveryChatWrite_WaitsForApproval(string operationId)
    {
        var executor = CreateExecutor(new AiToolRegistry([]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            operationId,
            Parse("""{"action":"unsupported_action"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, operationId);
        response.PendingOperation.Should().NotBeNull(operationId);
        response.PendingOperation!.ActionKey.Should().NotBeNullOrWhiteSpace(operationId);
        if (operationId == "set_proactive_astra")
            response.PendingOperation.ActionKey.Should().Be("setProactiveAstra");
        if (operationId is "bulk_create_habits" or "bulk_log_habits" or "bulk_skip_habits"
            or "bulk_update_habits" or "bulk_reschedule_habits")
        {
            response.Operation.RiskClass.Should().Be(AgentRiskClass.Low);
            response.PendingOperation.RiskClass.Should().Be(AgentRiskClass.Low);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ExemptOperation_RunsAtOnce()
    {
        var tool = new StubTool("suggest_breakdown");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "suggest_breakdown",
            Parse("""{"title":"Clean the flat"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(1);
        AgentChatWriteHold.ExemptOperationIds.Should().BeEquivalentTo(["suggest_breakdown"]);
    }

    [Theory]
    [InlineData("bulk_create_habits", """{"habits":[{"title":"New"},{"title":"Another"}]}""", false)]
    [InlineData("bulk_log_habits", """{"filter":{"all":true}}""", false)]
    [InlineData("bulk_skip_habits", """{"filter":{"all":true}}""", false)]
    [InlineData("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"✅"}}""", false)]
    [InlineData("bulk_reschedule_habits", """{"filter":{"all":true},"due_date":"2026-10-01"}""", false)]
    [InlineData("bulk_delete_habits", """{"filter":{"all":true}}""", true)]
    public async Task ExecuteAsync_BulkChatWrite_ReturnsApprovalPreviewWithRemovalFlags(
        string operationId, string arguments, bool removesData)
    {
        var today = new DateOnly(2026, 9, 25);
        _dbContext.Habits.AddRange(
            Habit.Create(new HabitCreateParams(_userId, "One", FrequencyUnit.Day, 1, today)).Value,
            Habit.Create(new HabitCreateParams(_userId, "Two", FrequencyUnit.Day, 1, today)).Value);
        await _dbContext.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var tool = new StubTool(operationId);
        var tools = new AiToolRegistry([tool]);
        var previewer = new PendingOperationChangePreviewer(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<Goal>(_dbContext),
            new GenericRepository<Tag>(_dbContext), dates, tools);
        var executor = CreateExecutor(tools, previewer);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, operationId, Parse(arguments), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            IncludeChangePreview: true));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        response.Operation.RiskClass.Should().Be(removesData ? AgentRiskClass.Destructive : AgentRiskClass.Low);
        response.PendingOperation!.RiskClass.Should().Be(response.Operation.RiskClass);
        response.PendingOperation.Items.Should().HaveCount(2);
        response.PendingOperation.Items.Should().OnlyContain(item => item.RemovesData == removesData);
        tool.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_LowRiskWriteOnMcpSurface_RunsAtOnce()
    {
        var tool = new StubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Beber agua","frequency_unit":"Day"}"""),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DeactivatedAccount_DeniesBeforeTheHold()
    {
        var user = _dbContext.Users.First(item => item.Id == _userId);
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        _dbContext.SaveChanges();
        var tool = new StubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Beber agua","frequency_unit":"Day"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        response.PolicyDenial!.Reason.Should().Be("account_deactivated");
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ToolThatAsksFirst_KeepsTheQuestionBeforeTheHold()
    {
        var tool = new ClarifyingStubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Rotina da manha"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        response.Operation.Payload.Should().BeOfType<NeedsClarificationPayload>();
        response.PendingOperation.Should().BeNull();
        tool.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AnsweredClarification_WaitsForApproval()
    {
        var tool = new ClarifyingStubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId,
            "create_habit",
            Parse("""{"title":"Rotina da manha","frequency_unit":"Day"}"""),
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        response.PendingOperation.Should().NotBeNull();
        tool.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ApprovedWrite_ConsumesTheConfirmation()
    {
        var tool = new StubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));
        var arguments = Parse("""{"title":"Beber agua","frequency_unit":"Day"}""");

        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, "create_habit", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        var confirmation = _pendingOperationStore.Confirm(_userId, held.PendingOperation!.Id);

        var executed = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, "create_habit", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation!.ConfirmationToken));

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        tool.Calls.Should().Be(1);
        _pendingOperationStore.GetExecution(_userId, held.PendingOperation.Id).Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WrongConfirmationToken_WritesNothing()
    {
        var tool = new StubTool("create_habit");
        var executor = CreateExecutor(new AiToolRegistry([tool]));
        var arguments = Parse("""{"title":"Beber agua","frequency_unit":"Day"}""");
        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, "create_habit", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        _pendingOperationStore.Confirm(_userId, held.PendingOperation!.Id);

        var executed = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, "create_habit", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            ConfirmationToken: "agc_not_the_issued_token"));

        executed.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        tool.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(true, true, AgentExecutionSurface.Chat)]
    [InlineData(true, false, AgentExecutionSurface.Chat)]
    [InlineData(false, true, AgentExecutionSurface.Chat)]
    [InlineData(false, false, AgentExecutionSurface.Mcp)]
    [InlineData(false, true, AgentExecutionSurface.Mcp)]
    public async Task ExecutePendingOperation_InitialPreview_BindsApprovalToTargetSet(
        bool includePreview, bool addHabit, AgentExecutionSurface surface)
    {
        var today = new DateOnly(2026, 9, 25);
        _dbContext.Habits.Add(
            Habit.Create(new HabitCreateParams(_userId, "One", FrequencyUnit.Day, 1, today)).Value);
        await _dbContext.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var tool = new StubTool("bulk_delete_habits");
        var tools = new AiToolRegistry([tool]);
        var previewer = new PendingOperationChangePreviewer(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<Goal>(_dbContext),
            new GenericRepository<Tag>(_dbContext), dates, tools);
        var executor = CreateExecutor(tools, previewer);
        var revisions = new PendingOperationRevisionService(_pendingOperationStore, previewer,
            new RevisePendingOperationRequestValidator(), tools);

        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            _userId, tool.Name, Parse("""{"filter":{"all":true}}"""),
            surface, AgentAuthMethod.Jwt, IncludeChangePreview: includePreview));

        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pending = held.PendingOperation!;
        if (includePreview)
        {
            pending.Items.Should().ContainSingle();
            pending.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        }
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        if (addHabit)
        {
            _dbContext.Habits.Add(
                Habit.Create(new HabitCreateParams(_userId, "Two", FrequencyUnit.Day, 1, today)).Value);
            await _dbContext.SaveChangesAsync();
        }
        _dbContext.ChangeTracker.Clear();

        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var shouldExecute = surface == AgentExecutionSurface.Mcp || !addHabit;
        (await revisions.IsCurrentAsync(_userId, execution, CancellationToken.None))
            .Should().Be(shouldExecute);
        execution.PreviewFingerprint.Should().Be(pending.PreviewFingerprint);
        var controller = new AiController(_catalogService, _policyEvaluator,
            new AgentPendingStores(_pendingOperationStore, Substitute.For<IPendingClarificationStore>()),
            Substitute.For<IAgentStepUpService>(), Substitute.For<IAgentAuditService>(),
            executor, revisions, new ResolveClarificationRequestValidator())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"))
                }
            }
        };

        var result = await controller.ExecutePendingOperation(pending.Id,
            new AiController.ExecutePendingOperationRequest(confirmation.ConfirmationToken), CancellationToken.None);

        if (shouldExecute)
        {
            result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
                .BeOfType<AgentExecuteOperationResponse>().Which.Operation.Status.Should()
                .Be(AgentOperationStatus.Succeeded);
            tool.Calls.Should().Be(1);
            _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
        }
        else
        {
            var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
            JsonSerializer.SerializeToElement(conflict.Value).GetProperty("error").GetString()
                .Should().Be("stale_preview");
            tool.Calls.Should().Be(0);
            _pendingOperationStore.GetExecution(_userId, pending.Id).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ExecuteAsync_EmptySelection_NeverReturnsAnApproval()
    {
        var tools = new AiToolRegistry([new StubTool("bulk_log_habits")]);
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(_userId, "bulk_log_habits", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(new PendingOperationChangePreview([], 0, [], "empty"));
        var executor = CreateExecutor(tools, previewer);

        var result = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId,
            "bulk_log_habits", Parse("""{"filter":{"search":"meditação"}}"""),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));

        result.PendingOperation.Should().BeNull();
        result.Operation.Status.Should().NotBe(AgentOperationStatus.PendingConfirmation);
    }

    [Fact]
    public async Task ExecuteAsync_CreateApproval_JsonStorageOrderAndUnrelatedChangeDoNotMakeItStale()
    {
        var today = new DateOnly(2026, 10, 19);
        var unrelated = Habit.Create(new HabitCreateParams(_userId, "Beber água", FrequencyUnit.Day, 1, today)).Value;
        _dbContext.Habits.Add(unrelated);
        await _dbContext.SaveChangesAsync();
        var tool = new StubTool("bulk_create_habits");
        var tools = new AiToolRegistry([tool]);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(_dbContext),
            new GenericRepository<Goal>(_dbContext), new GenericRepository<Tag>(_dbContext), dates, tools);
        var executor = CreateExecutor(tools, previewer);
        var arguments = Parse("""{"habits":[{"title":"Novo","frequency_unit":"Day"}]}""");
        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name,
            arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        var stored = _dbContext.PendingAgentOperations.Single(item => item.Id == held.PendingOperation!.Id);
        var reordered = """{"habits":[{"frequency_unit":"Day","title":"Novo"}]}""";
        stored.Revise(reordered, AgentOperationFingerprint.Compute(tool.Name, reordered),
            held.PendingOperation!.PreviewFingerprint!, DateTime.UtcNow).Should().BeTrue();
        unrelated.Update(new HabitUpdateParams("Changed unrelated habit", unrelated.Description, unrelated.FrequencyUnit,
            unrelated.FrequencyQuantity, Days: unrelated.Days.ToList(), IsBadHabit: unrelated.IsBadHabit, DueDate: unrelated.DueDate)).IsSuccess.Should().BeTrue();
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var revisions = new PendingOperationRevisionService(_pendingOperationStore, previewer,
            new RevisePendingOperationRequestValidator(), tools);
        var execution = _pendingOperationStore.GetExecution(_userId, held.PendingOperation.Id)!;

        (await revisions.IsCurrentAsync(_userId, execution, default)).Should().BeTrue();
        var confirmation = _pendingOperationStore.Confirm(_userId, held.PendingOperation.Id)!;
        var approved = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, execution.OperationId,
            execution.Arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        tool.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AssignTags_BatchApproval_ReusesOneTagAcrossExistingHabits()
    {
        var today = new DateOnly(2026, 10, 19);
        var habits = new[] { "Beber água", "Academia" }.Select(title =>
            Habit.Create(new HabitCreateParams(_userId, title, FrequencyUnit.Day, 1, today)).Value).ToArray();
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var tool = new Orbit.Application.Chat.Tools.Implementations.AssignTagsTool(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<Tag>(_dbContext),
            new UnitOfWork(_dbContext, new DatabaseConnectionSettings()));
        var arguments = JsonSerializer.SerializeToElement(new { habit_ids = habits.Select(habit => habit.Id).ToArray(), tag_names = new[] { "Saúde" } });

        (await tool.CheckArgumentsAsync(arguments, _userId, default)).IsSuccess.Should().BeTrue();
        var result = await tool.ExecuteAsync(arguments, _userId, default);

        result.Success.Should().BeTrue();
        _dbContext.ChangeTracker.Clear();
        var saved = await _dbContext.Habits.Include(habit => habit.Tags).Where(habit => habit.UserId == _userId).ToListAsync();
        saved.Should().HaveCount(2);
        saved.Should().OnlyContain(habit => habit.Tags.Count == 1 && habit.Tags.Single().Name == "Saúde");
        (await _dbContext.Tags.CountAsync(tag => tag.UserId == _userId)).Should().Be(1);
    }

    [Fact]
    public void PendingCreateProposals_KeepParentScopeAndExcludeOtherUsersAndCancelledWrites()
    {
        var parentId = Guid.NewGuid();
        var capability = _catalogService.GetCapabilityByChatTool("bulk_create_habits")!;
        var rootJson = """{"habits":[{"title":"Novo"}]}""";
        var childJson = JsonSerializer.Serialize(new { parent_habit_id = parentId, habits = new[] { new { title = "Treino" } } });
        var root = _pendingOperationStore.Create(_userId, capability, "bulk_create_habits", rootJson, "Create",
            AgentOperationFingerprint.Compute("bulk_create_habits", rootJson), AgentExecutionSurface.Chat);
        _pendingOperationStore.Create(_userId, capability, "bulk_create_habits", childJson, "Create children",
            AgentOperationFingerprint.Compute("bulk_create_habits", childJson), AgentExecutionSurface.Chat);
        _pendingOperationStore.Create(Guid.NewGuid(), capability, "bulk_create_habits", rootJson, "Other user",
            AgentOperationFingerprint.Compute("bulk_create_habits", rootJson), AgentExecutionSurface.Chat);

        _pendingOperationStore.GetPendingHabitCreations(_userId).Should().BeEquivalentTo(
            [new PendingHabitCreation("Novo"), new PendingHabitCreation("Treino", parentId)]);
        _pendingOperationStore.Cancel(_userId, root.Id,
            AgentOperationFingerprint.Compute("bulk_create_habits", rootJson)).Should().BeTrue();
        _pendingOperationStore.GetPendingHabitCreations(_userId).Should().Equal(new PendingHabitCreation("Treino", parentId));
    }

    [Fact]
    public async Task ReviseDateRange_KeepingOneDate_PreservesTheOfferedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(_userId, "Beber água", FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 19))).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();
        var tools = new AiToolRegistry([new StubTool("bulk_log_habits")]);
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(_dbContext),
            new GenericRepository<Goal>(_dbContext), new GenericRepository<Tag>(_dbContext), Substitute.For<IUserDateService>(), tools);
        var executor = CreateExecutor(tools, previewer);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            revised_items = new[]
        {
            new { habit_id = habit.Id, date = "2026-10-18" }, new { habit_id = habit.Id, date = "2026-10-19" }
        }
        });
        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "bulk_log_habits",
            arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        var pending = held.PendingOperation!;
        var chosen = pending.Items![0];
        var revisions = new PendingOperationRevisionService(_pendingOperationStore, previewer,
            new RevisePendingOperationRequestValidator(), tools);

        var result = await revisions.ReviseAsync(_userId, pending.Id,
            new RevisePendingOperationRequest(pending.PreviewFingerprint!, [new(chosen.ItemId)]), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Preview!.Items.Should().ContainSingle().Which.ItemId.Should().Be(chosen.ItemId);
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        execution.Arguments.GetProperty("revised_items").GetArrayLength().Should().Be(1);
        (await revisions.IsCurrentAsync(_userId, execution, default)).Should().BeTrue();
    }

    [Theory]
    [InlineData("create_goal")]
    [InlineData("assign_tags")]
    public async Task RoundThree_UnavailablePreview_RefusesEveryWrite(string operationId)
    {
        var tool = new StubTool(operationId);
        var executor = CreateExecutor(new AiToolRegistry([tool]), Substitute.For<IPendingOperationChangePreviewer>());
        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, operationId,
            Parse("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        response.PendingOperation.Should().BeNull();
        response.Operation.PolicyReason.Should().Be("stale_preview");
        tool.Calls.Should().Be(0);
        _dbContext.PendingAgentOperations.Should().OnlyContain(item => item.ConsumedAtUtc != null);
    }

    [Fact]
    public async Task RoundThree_MissingStoredFingerprint_CannotApprove()
    {
        var capability = _catalogService.GetCapabilityByChatTool("create_goal")!;
        var pending = _pendingOperationStore.Create(_userId, capability, "create_goal", "{}", "Create",
            AgentOperationFingerprint.Compute("create_goal", "{}"), AgentExecutionSurface.Chat);
        var tools = new AiToolRegistry([new StubTool("create_goal")]);
        var revisions = new PendingOperationRevisionService(_pendingOperationStore,
            Substitute.For<IPendingOperationChangePreviewer>(), new RevisePendingOperationRequestValidator(), tools);
        (await revisions.IsCurrentAsync(_userId, _pendingOperationStore.GetExecution(_userId, pending.Id)!, default))
            .Should().BeFalse();
    }

    [Fact]
    public void RoundThree_ConfirmedProposals_AreNotStillPending()
    {
        var capability = _catalogService.GetCapabilityByChatTool("bulk_create_habits")!;
        const string args = """{"habits":[{"title":"Novo"}]}""";
        var pending = _pendingOperationStore.Create(_userId, capability, "bulk_create_habits", args, "Create",
            AgentOperationFingerprint.Compute("bulk_create_habits", args), AgentExecutionSurface.Chat);
        _pendingOperationStore.Confirm(_userId, pending.Id).Should().NotBeNull();
        _pendingOperationStore.GetPendingHabitCreations(_userId).Should().BeEmpty();
    }

    private AgentOperationExecutor CreateExecutor(AiToolRegistry toolRegistry,
        IPendingOperationChangePreviewer? previewer = null)
    {
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>()).Returns((string?)null);
        var stepUpBridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUpBridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);

        if (previewer is null)
        {
            previewer = Substitute.For<IPendingOperationChangePreviewer>();
            previewer.PreviewAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
                .Returns(call => AgentArgumentPreview.Build(call.Arg<string>(), call.Arg<JsonElement>(), null, default));
        }

        return new AgentOperationExecutor(
            _catalogService,
            _policyEvaluator,
            Substitute.For<IAgentAuditService>(),
            ownership,
            stepUpBridge,
            toolRegistry,
            previewer,
            _pendingOperationStore,
            Substitute.For<IUnitOfWork>(),
            NullLogger<AgentOperationExecutor>.Instance);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private class StubTool(string name, bool isReadOnly = false) : IAiTool
    {
        public int Calls { get; private set; }

        public string Name => name;

        public string Description => name;

        public bool IsReadOnly => isReadOnly;

        public object GetParameterSchema() => new { type = "object" };

        public virtual Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolResult(true, EntityName: name));
        }
    }

    private sealed class ClarifyingStubTool(string name) : StubTool(name), IClarificationPrecheckTool
    {
        public bool NeedsClarification(JsonElement args) =>
            args.ValueKind == JsonValueKind.Object && !args.TryGetProperty("frequency_unit", out _);

        public override Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
        {
            base.ExecuteAsync(args, userId, ct);
            return Task.FromResult(new ToolResult(true, EntityName: name,
                Payload: new NeedsClarificationPayload("question", "frequency_unit", [])));
        }
    }
}
