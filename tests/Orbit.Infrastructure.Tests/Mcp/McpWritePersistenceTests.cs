using Result = Orbit.Domain.Common.Result;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Mcp;

public partial class McpWritePersistenceTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);
    private static readonly Type[] BackingTools = [typeof(CreateHabitTool), typeof(UpdateHabitTool), typeof(DeleteHabitTool),
        typeof(LogHabitTool), typeof(UnlogHabitTool), typeof(SkipHabitTool), typeof(UndoSkipHabitTool),
        typeof(UpdateChecklistTool), typeof(CreateSubHabitTool), typeof(DuplicateHabitTool), typeof(BulkCreateHabitsTool),
        typeof(BulkDeleteHabitsTool), typeof(BulkUpdateHabitsTool), typeof(BulkUpdateHabitEmojisTool),
        typeof(BulkRescheduleHabitsTool), typeof(BulkLogHabitsTool), typeof(BulkSkipHabitsTool), typeof(ReorderHabitsTool),
        typeof(MoveHabitParentTool), typeof(LinkGoalsToHabitTool), typeof(CreateGoalTool), typeof(UpdateGoalTool),
        typeof(DeleteGoalTool), typeof(UpdateGoalProgressTool), typeof(UpdateGoalStatusTool), typeof(ReorderGoalsTool),
        typeof(LinkHabitsToGoalTool), typeof(CreateTagTool), typeof(UpdateTagTool), typeof(DeleteTagTool), typeof(AssignTagsTool),
        typeof(CreateChecklistTemplateTool), typeof(UpdateChecklistTemplateTool), typeof(DeleteChecklistTemplateTool)];

    [Theory]
    [InlineData("CreateHabit")]
    [InlineData("UpdateHabit")]
    [InlineData("DeleteHabit")]
    [InlineData("LogHabit")]
    [InlineData("UnlogHabit")]
    [InlineData("SkipHabit")]
    [InlineData("UndoSkipHabit")]
    [InlineData("UpdateChecklist")]
    [InlineData("CreateSubHabit")]
    [InlineData("DuplicateHabit")]
    [InlineData("BulkCreateHabits")]
    [InlineData("BulkDeleteHabits")]
    [InlineData("BulkUpdateHabits")]
    [InlineData("BulkUpdateHabitEmojis")]
    [InlineData("BulkRescheduleHabits")]
    [InlineData("BulkLogHabits")]
    [InlineData("BulkSkipHabits")]
    [InlineData("ReorderHabits")]
    [InlineData("MoveHabitParent")]
    [InlineData("LinkGoalsToHabit")]
    [InlineData("CreateGoal")]
    [InlineData("UpdateGoal")]
    [InlineData("DeleteGoal")]
    [InlineData("UpdateGoalProgress")]
    [InlineData("UpdateGoalStatus")]
    [InlineData("ReorderGoals")]
    [InlineData("LinkHabitsToGoal")]
    [InlineData("CreateTag")]
    [InlineData("UpdateTag")]
    [InlineData("DeleteTag")]
    [InlineData("AssignTags")]
    [InlineData("CreateChecklistTemplate")]
    [InlineData("UpdateChecklistTemplate")]
    [InlineData("DeleteChecklistTemplate")]
    public async Task Wrapper_Success_PersistsThroughRealExecutor(string method)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var db = database.Context;
        var user = User.Create("MCP", "mcp@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read", FrequencyUnit.Day, 1, Today)).Value;
        var parent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Parent", FrequencyUnit.Day, 1, Today)).Value;
        var goal = Goal.Create(user.Id, "Books", 10, "books").Value;
        var tag = Tag.Create(user.Id, "Health", "#123456").Value;
        var template = ChecklistTemplate.Create(user.Id, "Morning", ["Water"]).Value;
        habit.SetPosition(7);
        goal.UpdatePosition(7);
        db.AddRange(user, habit, parent, goal, tag, template);
        await db.SaveChangesAsync();
        using var services = Services(db);
        var mediator = services.GetRequiredService<IMediator>();
        var tools = BackingTools.Select(type => (IAiTool)services.GetRequiredService(type)).ToArray();
        var catalog = new AgentCatalogService(tools);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.EvaluateAsync(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call => new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!));
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            new AgentTargetOwnershipService(db), Substitute.For<IAgentStepUpAuthorizationBridge>(), new AiToolRegistry(tools),
            Substitute.For<IPendingOperationChangePreviewer>(), Substitute.For<IPendingAgentOperationStore>(),
            services.GetRequiredService<IUnitOfWork>(), NullLogger<AgentOperationExecutor>.Instance);
        var bridge = new McpExecutorBridge(executor);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"));
        var habitTools = new HabitTools(mediator, services.GetRequiredService<IUserDateService>(), bridge);
        if (method == "UnlogHabit")
            (await habitTools.LogHabit(principal, habit.Id.ToString())).IsError.Should().NotBeTrue();
        Guid? skipId = null;
        if (method == "UndoSkipHabit")
        {
            (await habitTools.SkipHabit(principal, habit.Id.ToString())).IsError.Should().NotBeTrue();
            skipId = (await db.Set<HabitSkipUndo>().SingleAsync()).Id;
        }
        object target = method.Contains("Goal", StringComparison.Ordinal) && method != "LinkGoalsToHabit"
            ? new GoalTools(mediator, bridge) : method.Contains("Tag", StringComparison.Ordinal) ? new TagTools(mediator, bridge)
            : method.Contains("Template", StringComparison.Ordinal) ? new ChecklistTemplateTools(mediator, bridge) : habitTools;
        var args = Arguments(method, habit.Id, parent.Id, goal.Id, tag.Id, template.Id, skipId);
        var sdkTool = McpServerTool.Create(target.GetType().GetMethod(method)!, target);
        var request = new RequestContext<CallToolRequestParams>(Substitute.For<McpServer>(),
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" }, new CallToolRequestParams
            { Name = sdkTool.ProtocolTool.Name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)) })
        { User = principal, Services = services };
        var result = await sdkTool.InvokeAsync(request);
        result.IsError.Should().NotBeTrue(result.AsText());
        db.ChangeTracker.Clear();
        await AssertPersisted(db, method, habit.Id, parent.Id, goal.Id, tag.Id, template.Id);
    }

    private static ServiceProvider Services(OrbitDbContext db)
    {
        var services = new ServiceCollection().AddLogging().AddMemoryCache();
        var assembly = typeof(CreateHabitCommand).Assembly;
        services.AddMediatR(options => options.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly);
        foreach (var type in typeof(IGenericRepository<>).Assembly.GetTypes().Concat(assembly.GetTypes())
            .Where(type => type.IsInterface && type.IsPublic && !type.ContainsGenericParameters))
            services.AddSingleton(type, _ => Substitute.For([type], []));
        foreach (var type in assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && type.IsPublic
            && !type.ContainsGenericParameters && type.GetConstructors().Length > 0))
            services.AddTransient(type);
        services.AddSingleton(db);
        services.AddSingleton(new DatabaseConnectionSettings());
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);
        services.AddSingleton(dates);
        var quota = Substitute.For<IPayGateService>();
        quota.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        services.AddSingleton(quota);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<int>());
        services.AddSingleton(config);
        services.AddScoped<IGoalCompletionService, GoalCompletionService>();
        return services.BuildServiceProvider();
    }
}
