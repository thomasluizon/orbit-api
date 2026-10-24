#pragma warning disable OPENAI001

using System.Runtime.CompilerServices;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using OpenAI.Responses;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Tests.AI;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Validators;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Mcp;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;
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

        if (operationId == AccountChangeSet.OperationName)
        {
            response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
            response.Operation.PolicyReason.Should().Be("invalid_change_set");
            return;
        }
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

    [Theory]
    [InlineData("template", false)]
    [InlineData("name", false)]
    [InlineData("selection", false)]
    [InlineData("selection", true)]
    public async Task CapabilityParityWrites_ShowPreviewBeforeApprovalAndDispatchOnlyAfterConfirmation(string action, bool reset)
    {
        var template = ChecklistTemplate.Create(_userId, "Morning", ["Water"]).Value;
        _dbContext.ChecklistTemplates.Add(template);
        var user = await _dbContext.Users.SingleAsync(u => u.Id == _userId);
        user.SetSelectedCalendars(["primary"]);
        await _dbContext.SaveChangesAsync();
        var mediator = Substitute.For<IMediator>();
        var templates = new GenericRepository<ChecklistTemplate>(_dbContext);
        using var services = new ServiceCollection()
            .AddSingleton<IGenericRepository<ChecklistTemplate>>(templates)
            .AddSingleton<IValidator<UpdateChecklistTemplateCommand>>(new UpdateChecklistTemplateCommandValidator())
            .AddSingleton<IValidator<SetNameCommand>>(new SetNameCommandValidator())
            .AddSingleton<IValidator<SetSelectedCalendarsCommand>>(new SetSelectedCalendarsCommandValidator())
            .BuildServiceProvider();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => new CheckChatCommandQueryHandler(services).Handle(call.Arg<CheckChatCommandQuery>(), default));
        mediator.Send(Arg.Any<UpdateChecklistTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<SetNameCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        mediator.Send(Arg.Any<SetSelectedCalendarsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        IAiTool tool = action switch
        {
            "template" => new UpdateChecklistTemplateTool(mediator),
            "name" => new UpdateProfilePreferencesTool(mediator),
            _ => new ManageCalendarSyncTool(mediator)
        };
        var arguments = action switch
        {
            "template" => JsonSerializer.SerializeToElement(new { template_id = template.Id, name = "Evening", items = new[] { "Read" } }),
            "name" => JsonSerializer.SerializeToElement(new { action = "set_name", name = "Ana" }),
            _ => JsonSerializer.SerializeToElement(new { action = "set_selected_calendars", calendar_ids = reset ? Array.Empty<string>() : new[] { "work" } })
        };
        var checkedArgs = await ((IArgumentCheckTool)tool).CheckArgumentsAsync(arguments, _userId, default);
        checkedArgs.IsSuccess.Should().BeTrue(checkedArgs.Error);
        mediator.ClearReceivedCalls();
        var tools = new AiToolRegistry([tool]);
        var previewer = new PendingOperationChangePreviewer(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<Goal>(_dbContext),
            new GenericRepository<Tag>(_dbContext), Substitute.For<IUserDateService>(), tools,
            new DestructiveOperationPreviewer(_dbContext), templates);
        var executor = CreateExecutor(tools, previewer);
        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name, arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));

        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pending = held.PendingOperation!;
        pending.Items.Should().ContainSingle();
        pending.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        var field = pending.Items![0].Fields.Single(change => change.Field == (action == "selection" ? "calendar_ids" : "name"));
        if (action == "selection")
        {
            field.OldValue.Should().Be("[\"primary\"]");
            field.ProposedValue!.Value.GetArrayLength().Should().Be(reset ? 0 : 1);
        }
        else
        {
            field.NewValue.Should().Be(action == "template" ? "Evening" : "Ana");
            field.IsEditable.Should().BeTrue();
        }
        if (action == "template")
            field.OldValue.Should().Be("Morning");
        mediator.ReceivedCalls().Should().ContainSingle().Which.GetArguments()[0].Should().BeOfType<CheckChatCommandQuery>();
        template.Name.Should().Be("Morning");
        user.Name.Should().Be("Alex");
        user.GetSelectedCalendarIds().Should().Equal("primary");

        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var executed = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name, arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        mediator.ReceivedCalls().Where(call => call.GetArguments()[0] is not CheckChatCommandQuery).Should().ContainSingle();
    }

    [Theory]
    [InlineData("name", ValidationErrorCodes.ProfileNameLength)]
    [InlineData("template_limit", ValidationErrorCodes.TemplateItemLimit)]
    [InlineData("template_length", ValidationErrorCodes.TemplateItemLength)]
    [InlineData("selection", ValidationErrorCodes.CalendarSelectionLimit)]
    public async Task CapabilityParityWrites_InvalidArgumentsAreRejectedBeforePreviewOnChatAndMcp(string action, string code)
    {
        var template = ChecklistTemplate.Create(_userId, "Morning", ["Water"]).Value;
        _dbContext.ChecklistTemplates.Add(template);
        await _dbContext.SaveChangesAsync();
        var mediator = Substitute.For<IMediator>();
        using var services = new ServiceCollection()
            .AddSingleton<IGenericRepository<ChecklistTemplate>>(new GenericRepository<ChecklistTemplate>(_dbContext))
            .AddValidatorsFromAssemblyContaining<SetNameCommandValidator>()
            .BuildServiceProvider();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => new CheckChatCommandQueryHandler(services).Handle(call.Arg<CheckChatCommandQuery>(), default));
        mediator.Send(Arg.Any<SetNameCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValidationBehavior<SetNameCommand, Result>([new SetNameCommandValidator()])
                .Handle(call.Arg<SetNameCommand>(), _ => Task.FromResult(Result.Success()), default));
        mediator.Send(Arg.Any<UpdateChecklistTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValidationBehavior<UpdateChecklistTemplateCommand, Result>([new UpdateChecklistTemplateCommandValidator()])
                .Handle(call.Arg<UpdateChecklistTemplateCommand>(), _ => Task.FromResult(Result.Success()), default));
        mediator.Send(Arg.Any<SetSelectedCalendarsCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValidationBehavior<SetSelectedCalendarsCommand, Result>([new SetSelectedCalendarsCommandValidator()])
                .Handle(call.Arg<SetSelectedCalendarsCommand>(), _ => Task.FromResult(Result.Success()), default));
        IAiTool tool = action switch
        {
            "name" => new UpdateProfilePreferencesTool(mediator),
            "selection" => new ManageCalendarSyncTool(mediator),
            _ => new UpdateChecklistTemplateTool(mediator)
        };
        var name = new string('a', 51);
        var items = action == "template_limit" ? Enumerable.Repeat("Item", AppConstants.MaxChecklistItems + 1).ToArray()
            : new[] { new string('a', AppConstants.MaxChecklistItemTextLength + 1) };
        var ids = Enumerable.Range(0, AppConstants.MaxSelectedCalendars + 1).Select(i => $"calendar-{i}").ToArray();
        var arguments = action switch
        {
            "name" => JsonSerializer.SerializeToElement(new { action = "set_name", name }),
            "selection" => JsonSerializer.SerializeToElement(new { action = "set_selected_calendars", calendar_ids = ids }),
            _ => JsonSerializer.SerializeToElement(new { template_id = template.Id, items })
        };
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(_userId, tool.Name, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(AgentArgumentPreview.Build(tool.Name, call.Arg<JsonElement>(), null, default)));
        var executor = CreateExecutor(new AiToolRegistry([tool]), previewer);

        var chat = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name, arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"));
        var bridge = new McpExecutorBridge(executor);
        var mcp = action switch
        {
            "name" => await new Orbit.Api.Mcp.Tools.ProfileTools(mediator, bridge).SetName(user, name),
            "selection" => await new Orbit.Api.Mcp.Tools.CalendarTools(mediator, bridge).SetSelectedCalendars(user, ids),
            _ => await new Orbit.Api.Mcp.Tools.ChecklistTemplateTools(mediator, bridge)
                .UpdateChecklistTemplate(user, template.Id.ToString(), items: items)
        };

        chat.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        chat.Operation.PolicyReason.Should().Be(code);
        chat.PendingOperation.Should().BeNull();
        mcp.Should().Be($"Error: {code}");
        await previewer.DidNotReceive().PreviewAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        _dbContext.PendingAgentOperations.Should().BeEmpty();
        mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
        template.Items.Should().Equal("Water");
    }

    [Fact]
    public async Task ExecuteAsync_EmptySelection_NeverReturnsAnApproval()
    {
        var tools = new AiToolRegistry([new StubTool("bulk_log_habits")]);
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(_userId, "bulk_log_habits", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<PendingOperationChangePreview?>(new PendingOperationChangePreview([], 0, [], "empty")));
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
            unrelated.FrequencyQuantity, Days: unrelated.Days.ToList(), IsBadHabit: unrelated.IsBadHabit, DueDate: unrelated.NextDueDate)).IsSuccess.Should().BeTrue();
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
        var child = _pendingOperationStore.Create(_userId, capability, "bulk_create_habits", childJson, "Create children",
            AgentOperationFingerprint.Compute("bulk_create_habits", childJson), AgentExecutionSurface.Chat);
        _pendingOperationStore.Create(Guid.NewGuid(), capability, "bulk_create_habits", rootJson, "Other user",
            AgentOperationFingerprint.Compute("bulk_create_habits", rootJson), AgentExecutionSurface.Chat);

        _pendingOperationStore.GetPendingHabitCreations(_userId).Should().BeEquivalentTo(
            [new PendingHabitCreation("Novo", PendingOperationId: root.Id, ExpiresAtUtc: root.ExpiresAtUtc, ActionKey: "createHabits"),
             new PendingHabitCreation("Treino", parentId, child.Id, child.ExpiresAtUtc, "createHabits")]);
        _pendingOperationStore.Cancel(_userId, root.Id,
            AgentOperationFingerprint.Compute("bulk_create_habits", rootJson)).Should().BeTrue();
        _pendingOperationStore.GetPendingHabitCreations(_userId).Should().Equal(
            new PendingHabitCreation("Treino", parentId, child.Id, child.ExpiresAtUtc, "createHabits"));
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
        var previewer = Substitute.For<IPendingOperationChangePreviewer>();
        previewer.PreviewAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<PendingOperationChangePreview?>(null));
        var executor = CreateExecutor(new AiToolRegistry([tool]), previewer);
        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, operationId,
            Parse("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        response.PendingOperation.Should().BeNull();
        response.Operation.PolicyReason.Should().Be("preview_unavailable");
        tool.Calls.Should().Be(0);
        _dbContext.PendingAgentOperations.Should().OnlyContain(item => item.ConsumedAtUtc != null);
    }

    [Theory]
    [InlineData(25, "approve")]
    [InlineData(26, "approve")]
    [InlineData(55, "approve")]
    [InlineData(174, "approve")]
    [InlineData(26, "edit")]
    [InlineData(26, "reject")]
    [InlineData(26, "stale")]
    public async Task InferredIcons_AllTargets_RequireCompleteBoundApprovalAndReadBackExactly(int count, string action)
    {
        var (habits, completed, foreign) = await CreateIconHabitsAsync(count);
        var heldState = habits.Concat([completed, foreign]).ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit));
        var originalState = habits.ToDictionary(habit => habit.Id, StateExceptIcon);
        var originalVersions = habits.ToDictionary(habit => habit.Id, habit => habit.ReminderProbeVersion);
        var expected = habits.Select((habit, index) => (habit.Id, Emoji: index % 2 == 0 ? "📚" : "🏃"))
            .ToDictionary(item => item.Id, item => item.Emoji);
        var handler = new IconResponseHandler(habits.Chunk(25).Select(chunk => ResponsesTestData.Text(JsonSerializer.Serialize(new
        {
            emojis = chunk.ToDictionary(habit => habit.Id.ToString(), habit => expected[habit.Id])
        }))));
        var (tool, executor, revisions) = CreateIconExecutor(handler);

        var held = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name,
            Parse("""{"emoji_mode":"infer","filter":{"all":true}}"""), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pending = held.PendingOperation!;
        _dbContext.PendingAgentOperations.Should().ContainSingle();
        pending.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
        pending.ChangeTargetCount.Should().Be(count);
        pending.Items.Should().HaveCount(count);
        pending.Items!.Select(item => item.EntityId).Should().BeEquivalentTo(habits.Select(habit => (Guid?)habit.Id));
        foreach (var item in pending.Items!)
        {
            var field = item.Fields.Should().ContainSingle(change => change.Field == "emoji").Which;
            field.NewValue.Should().Be(expected[item.EntityId!.Value]);
            field.ProposedValue!.Value.GetString().Should().Be(expected[item.EntityId.Value]);
            field.IsEditable.Should().BeTrue();
        }
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Habits.Include(habit => habit.Logs).ToListAsync()).ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit))
            .Should().BeEquivalentTo(heldState);
        AssertIconRequests(handler, habits, (count + 24) / 25);
        var refreshed = await revisions.RefreshAsync(_userId, pending.Id, default);
        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Preview!.PreviewFingerprint.Should().Be(pending.PreviewFingerprint);
        refreshed.Preview.Items.Should().HaveCount(count);
        handler.Calls.Should().Be((count + 24) / 25);
        if (action == "reject")
        {
            var rejected = await revisions.ReviseAsync(_userId, pending.Id,
                new RevisePendingOperationRequest(pending.PreviewFingerprint!, []), default);
            rejected.Cancelled.Should().BeTrue();
            _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
            handler.Calls.Should().Be((count + 24) / 25);
            return;
        }
        if (action == "edit")
        {
            var chosen = pending.Items[0];
            var revised = await revisions.ReviseAsync(_userId, pending.Id, new RevisePendingOperationRequest(
                pending.PreviewFingerprint!, [new(chosen.ItemId, Parse("""{"emoji":"🌟"}"""))]), default);
            revised.IsSuccess.Should().BeTrue(revised.Error);
            expected = new Dictionary<Guid, string> { [chosen.EntityId!.Value] = "🌟" };
        }
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        if (action == "stale")
        {
            var changed = await _dbContext.Habits.SingleAsync(habit => habit.Id == habits[0].Id);
            changed.Update(new HabitUpdateParams("Changed title", changed.Description, changed.FrequencyUnit,
                changed.FrequencyQuantity, changed.Days.ToList(), changed.IsBadHabit, changed.NextDueDate, Emoji: changed.Emoji)).IsSuccess.Should().BeTrue();
            await _dbContext.SaveChangesAsync();
        }
        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name,
            execution.Arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        response.Operation.Status.Should().Be(action == "stale" ? AgentOperationStatus.Denied : AgentOperationStatus.Succeeded);
        if (action == "stale") response.PolicyDenial!.Reason.Should().Be("stale_preview");
        else
        {
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(response.Operation.Payload), JsonSerializer.SerializeToElement(new
            {
                applied_count = expected.Count,
                total_matched = expected.Count,
                skipped_count = 0,
                partial = false,
                updated_count = expected.Count
            })).Should().BeTrue();
            _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
        }
        _dbContext.ChangeTracker.Clear();
        var readback = await _dbContext.Habits.Include(habit => habit.Logs).ToListAsync();
        readback.Select(habit => habit.Id).Should().BeEquivalentTo(habits.Concat([completed, foreign]).Select(habit => habit.Id));
        foreach (var habit in readback.Where(habit => habits.Any(original => original.Id == habit.Id)))
        {
            habit.Emoji.Should().Be(action != "stale" && expected.TryGetValue(habit.Id, out var emoji) ? emoji : "📝");
            if (action != "stale")
            {
                StateExceptIcon(habit).Should().Be(originalState[habit.Id]);
                habit.ReminderProbeVersion.Should().Be(originalVersions[habit.Id] + (expected.ContainsKey(habit.Id) ? 1 : 0));
            }
        }
        readback.Single(habit => habit.Id == foreign.Id).Emoji.Should().Be("🔒");
        readback.Single(habit => habit.Id == completed.Id).Emoji.Should().Be("✅");
        JsonSerializer.Serialize(readback.Single(habit => habit.Id == foreign.Id)).Should().Be(heldState[foreign.Id]);
        JsonSerializer.Serialize(readback.Single(habit => habit.Id == completed.Id)).Should().Be(heldState[completed.Id]);
        if (action != "stale")
        {
            var approvedState = readback.ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit));
            var replay = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name,
                execution.Arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
            replay.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
            _dbContext.ChangeTracker.Clear();
            (await _dbContext.Habits.Include(habit => habit.Logs).ToListAsync()).ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit))
                .Should().BeEquivalentTo(approvedState);
        }
        handler.Calls.Should().Be((count + 24) / 25);
    }

    [Fact]
    public Task InferredIcons_SevenChunks_SixthMappingMissingTargetStopsBeforeSeventhDispatch() =>
        AssertLateIconPreparationFailureAsync(budgetExceeded: false);

    [Fact]
    public Task InferredIcons_SevenChunks_SeventhBudgetReservationPreservesTypedFailure() =>
        AssertLateIconPreparationFailureAsync(budgetExceeded: true);

    private async Task AssertLateIconPreparationFailureAsync(bool budgetExceeded)
    {
        var (habits, completed, foreign) = await CreateIconHabitsAsync(174);
        var original = habits.Concat([completed, foreign]).ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit));
        var handler = new IconResponseHandler(habits.Chunk(25).Select((chunk, index) => ResponsesTestData.Text(JsonSerializer.Serialize(new
        {
            emojis = chunk.Where((_, offset) => budgetExceeded || index != 5 || offset != 24)
                .ToDictionary(habit => habit.Id.ToString(), _ => "📚")
        }))));
        var reservations = new List<Guid>();
        var reservationAttempts = 0;
        var budget = Substitute.For<IAiDailyBudget>();
        budget.ReserveAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++reservationAttempts == 7 && budgetExceeded)
                return Task.FromException<Guid>(new AiBudgetExceededException());
            var reservation = Guid.NewGuid();
            reservations.Add(reservation);
            return Task.FromResult(reservation);
        });
        var usage = Substitute.For<IAiUsageRecorder>();
        usage.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(),
            Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<AiCallTelemetry?>(), Arg.Any<long>()).Returns(_ =>
        {
            AiCallContext.Current.Should().NotBeNull();
            AiCallContext.Current!.ReportCost(100L);
            return Task.CompletedTask;
        });
        var inferenceLogger = Substitute.For<ILogger<AiHabitEmojiInferenceService>>();
        inferenceLogger.IsEnabled(LogLevel.Warning).Returns(true);
        var preparationLogger = Substitute.For<ILogger<BulkUpdateHabitEmojisTool>>();
        preparationLogger.IsEnabled(LogLevel.Warning).Returns(true);
        var (tool, executor, _) = CreateIconExecutor(handler, budget, usage, inferenceLogger, preparationLogger);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, tool.Name,
            Parse("""{"emoji_mode":"infer","filter":{"all":true}}"""), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        response.Operation.PolicyReason.Should().Be(budgetExceeded ? "AI_DAILY_BUDGET_EXCEEDED" : "preview_unavailable");
        response.PendingOperation.Should().BeNull();
        response.PolicyDenial.Should().BeNull();
        var pending = _dbContext.PendingAgentOperations.Should().ContainSingle().Which;
        pending.PreviewFingerprint.Should().BeNull();
        pending.ConfirmedAtUtc.Should().BeNull();
        pending.ConsumedAtUtc.Should().NotBeNull();
        _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
        _dbContext.ChangeTracker.Entries<Habit>().Should().NotContain(entry => entry.State != EntityState.Unchanged);
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Habits.Include(habit => habit.Logs).ToListAsync()).ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit))
            .Should().BeEquivalentTo(original);
        AssertIconRequests(handler, habits, 6);
        await budget.Received(budgetExceeded ? 7 : 6).ReserveAsync(_userId, 1000L, Arg.Any<CancellationToken>());
        reservations.Should().HaveCount(6).And.OnlyHaveUniqueItems();
        await budget.Received(6).SettleAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        foreach (var reservation in reservations)
            await budget.Received(1).SettleAsync(reservation, 100L, Arg.Any<CancellationToken>());
        usage.ReceivedCalls().Should().HaveCount(6);
        await usage.Received(6).RecordAsync("habit_emoji_inference", "gpt-6-luna", 0, 1, 2, 3,
            Arg.Any<CancellationToken>(), _userId, Arg.Is<AiCallTelemetry>(call => call.HasUsage && !call.IsError), 0);
        AssertIconFailureReceipt(inferenceLogger, new Dictionary<string, object?>
        {
            ["Stage"] = budgetExceeded ? "completion" : "mapping",
            ["Reason"] = budgetExceeded ? "budget_exceeded" : "membership_mismatch",
            ["ChunkTargetCount"] = budgetExceeded ? 24 : 25
        });
        AssertIconFailureReceipt(preparationLogger, new Dictionary<string, object?>
        {
            ["Stage"] = "inference",
            ["Reason"] = budgetExceeded ? "budget_exceeded" : "unknown",
            ["ChunkIndex"] = budgetExceeded ? 7 : 6,
            ["ChunkCount"] = 7,
            ["ChunkTargetCount"] = budgetExceeded ? 24 : 25,
            ["TargetCount"] = 174
        });
    }

    private async Task<(Habit[] Habits, Habit Completed, Habit Foreign)> CreateIconHabitsAsync(int count)
    {
        var today = new DateOnly(2026, 10, 19);
        var habits = Enumerable.Range(0, count).Select(index => Habit.Create(new HabitCreateParams(_userId,
            $"Habit {index:D3}", FrequencyUnit.Day, 1, today, Description: "Keep description", DueTime: new TimeOnly(9, 0),
            Days: [DayOfWeek.Monday], ChecklistItems: [new("Keep checklist", true)], Emoji: "📝")).Value).ToArray();
        var completed = Habit.Create(new HabitCreateParams(_userId, "Completed", null, null, today, Emoji: "✅")).Value;
        completed.Log(today);
        var foreign = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Foreign", FrequencyUnit.Day, 1, today, Emoji: "🔒")).Value;
        _dbContext.Habits.AddRange(habits.Concat([completed, foreign]));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return (habits, completed, foreign);
    }

    private (BulkUpdateHabitEmojisTool Tool, AgentOperationExecutor Executor, PendingOperationRevisionService Revisions) CreateIconExecutor(
        IconResponseHandler handler, IAiDailyBudget? budget = null, IAiUsageRecorder? usage = null,
        ILogger<AiHabitEmojiInferenceService>? inferenceLogger = null, ILogger<BulkUpdateHabitEmojisTool>? preparationLogger = null)
    {
        var client = new AiCompletionClient(new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        }), NullLogger<AiCompletionClient>.Instance, usage ?? Substitute.For<IAiUsageRecorder>());
        var budgeted = AiBudgetTestDouble.Wrap(client);
        if (budget is not null)
        {
            var estimates = Substitute.For<IAiRequestCostEstimator>();
            estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1000L);
            budgeted = new BudgetedAiCompletionClient(client, budget, new HttpContextAccessor(),
                NullLogger<BudgetedAiCompletionClient>.Instance, estimates);
        }
        var inference = new AiHabitEmojiInferenceService(budgeted, inferenceLogger ?? NullLogger<AiHabitEmojiInferenceService>.Instance);
        var tool = new BulkUpdateHabitEmojisTool(new GenericRepository<Habit>(_dbContext), inference,
            new UnitOfWork(_dbContext, new DatabaseConnectionSettings()), preparationLogger ?? NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var tools = new AiToolRegistry([tool]);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 19));
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(_dbContext),
            new GenericRepository<Goal>(_dbContext), new GenericRepository<Tag>(_dbContext), dates, tools);
        return (tool, CreateExecutor(tools, previewer), new PendingOperationRevisionService(_pendingOperationStore, previewer,
            new RevisePendingOperationRequestValidator(), tools));
    }

    private static void AssertIconRequests(IconResponseHandler handler, Habit[] habits, int completionCount)
    {
        handler.Calls.Should().Be(completionCount);
        handler.Requests.Should().HaveCount(completionCount);
        var chunks = habits.Chunk(25).Take(completionCount).ToArray();
        for (var index = 0; index < completionCount; index++)
        {
            using var json = JsonDocument.Parse(handler.Requests[index]);
            json.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
            json.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(1024);
            json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
            var userInput = json.RootElement.GetProperty("input").EnumerateArray()
                .Single(item => item.GetProperty("role").GetString() == "user");
            using var prompt = JsonDocument.Parse(userInput.GetProperty("content")[0].GetProperty("text").GetString()!);
            prompt.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetGuid())
                .Should().Equal(chunks[index].Select(habit => habit.Id));
        }
    }

    private static void AssertIconFailureReceipt<T>(ILogger<T> logger, Dictionary<string, object?> expected)
    {
        var entry = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)).Should().ContainSingle().Which;
        entry.GetArguments()[0].Should().Be(LogLevel.Warning);
        entry.GetArguments()[3].Should().BeNull();
        ((IEnumerable<KeyValuePair<string, object?>>)entry.GetArguments()[2]!).Where(pair => pair.Key != "{OriginalFormat}")
            .Should().BeEquivalentTo(expected);
    }

    private static string StateExceptIcon(Habit habit) => JsonSerializer.Serialize(
        JsonSerializer.SerializeToElement(habit).EnumerateObject()
            .Where(property => property.Name is not (nameof(Habit.Emoji) or nameof(Habit.UpdatedAtUtc) or nameof(Habit.ReminderProbeVersion)))
            .ToDictionary(property => property.Name, property => property.Value));

    private sealed class IconResponseHandler(IEnumerable<string> responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public int Calls { get; private set; }
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_responses.Dequeue(), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InferredIcons_InitialPreparationFailure_IsFailedAndCancelledWithoutStaleApproval(bool budget)
    {
        var habit = Habit.Create(new HabitCreateParams(_userId, "Read", FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 19))).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(budget
                ? Result.Failure<IReadOnlyDictionary<Guid, string>>(Orbit.Infrastructure.AI.AiBudgetExceededException.Error)
                : Result.Success<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()));
        var work = Substitute.For<IUnitOfWork>();
        var tools = new AiToolRegistry([new BulkUpdateHabitEmojisTool(new GenericRepository<Habit>(_dbContext),
            inference, work, NullLogger<BulkUpdateHabitEmojisTool>.Instance)]);
        var dates = Substitute.For<IUserDateService>();
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(_dbContext),
            new GenericRepository<Goal>(_dbContext), new GenericRepository<Tag>(_dbContext), dates, tools);
        var executor = CreateExecutor(tools, previewer);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId,
            "bulk_update_habit_emojis", Parse("""{"emoji_mode":"infer","filter":{"all":true}}"""),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        response.Operation.PolicyReason.Should().Be(budget ? "AI_DAILY_BUDGET_EXCEEDED" : "preview_unavailable");
        response.PendingOperation.Should().BeNull();
        response.PolicyDenial.Should().BeNull();
        var pending = _dbContext.PendingAgentOperations.Single();
        pending.PreviewFingerprint.Should().BeNull();
        pending.ConfirmedAtUtc.Should().BeNull();
        pending.ConsumedAtUtc.Should().NotBeNull();
        _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Habits.SingleAsync()).Emoji.Should().BeNull();
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InferredIcons_CancellationOrDeadline_CancelsTheUnboundProposal(bool callerCancelled)
    {
        _dbContext.Habits.Add(Habit.Create(new HabitCreateParams(_userId, "Read", FrequencyUnit.Day, 1,
            new DateOnly(2026, 10, 19))).Value);
        await _dbContext.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(_userId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (callerCancelled) cancellation.Cancel();
                return Task.FromException<Result<IReadOnlyDictionary<Guid, string>>>(new OperationCanceledException(cancellation.Token));
            });
        var work = Substitute.For<IUnitOfWork>();
        var tools = new AiToolRegistry([new BulkUpdateHabitEmojisTool(new GenericRepository<Habit>(_dbContext), inference,
            work, NullLogger<BulkUpdateHabitEmojisTool>.Instance)]);
        var previewer = new PendingOperationChangePreviewer(new GenericRepository<Habit>(_dbContext),
            new GenericRepository<Goal>(_dbContext), new GenericRepository<Tag>(_dbContext), Substitute.For<IUserDateService>(), tools);
        var executor = CreateExecutor(tools, previewer);
        var act = () => executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "bulk_update_habit_emojis",
            Parse("""{"filter":{"all":true}}"""), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), cancellation.Token);

        if (callerCancelled)
            await act.Should().ThrowAsync<OperationCanceledException>();
        else
        {
            var response = await act();
            response.Operation.PolicyReason.Should().Be("preview_unavailable");
            response.PendingOperation.Should().BeNull();
        }
        var pending = _dbContext.PendingAgentOperations.Single();
        pending.ConsumedAtUtc.Should().NotBeNull();
        pending.ConfirmedAtUtc.Should().BeNull();
        _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
        await work.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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
                .Returns(call => Result.Success(AgentArgumentPreview.Build(call.Arg<string>(), call.Arg<JsonElement>(), null, default)));
        }

        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<AgentExecuteOperationResponse>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<AgentExecuteOperationResponse>>>(0)(call.ArgAt<CancellationToken>(1)));
        return new AgentOperationExecutor(
            _catalogService,
            _policyEvaluator,
            Substitute.For<IAgentAuditService>(),
            ownership,
            stepUpBridge,
            toolRegistry,
            previewer,
            _pendingOperationStore,
            work,
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
