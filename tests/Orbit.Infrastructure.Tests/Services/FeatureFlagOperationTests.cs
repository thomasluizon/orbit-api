using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Orbit.Api.Authorization;
using Orbit.Api.Extensions;
using Orbit.Infrastructure.Tests.Authentication;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Application.FeatureFlags.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class FeatureFlagOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpWrite_UsesPolicyValidationPersistenceInvalidationAndAudit(bool oauthFamily)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FeatureFlagService(context, cache, Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build());
        var admin = User.Create("Admin", "admin@example.com").Value;
        admin.GrantAdmin();
        var account = User.Create("Account", "account@example.com").Value;
        context.Users.AddRange(admin, account);
        await context.SaveChangesAsync();
        (await resolver.GetEnabledKeysForUserAsync(account.Id)).Should().NotContain("astra_eval_trace");
        var services = new ServiceCollection().AddLogging();
        services.AddAuthorization(options => options.AddPolicy(AdminPolicy.Name,
            policy => policy.AddRequirements(new AdminRequirement())));
        services.AddSingleton<IAuthorizationHandler>(new AdminAuthorizationHandler(new GenericRepository<User>(context)));
        services.AddMcpServer().WithTools<FeatureFlagTools>().WithFeatureFlagAccess();
        using var provider = services
            .AddScoped<OrbitDbContext>(_ => factory.CreateContext())
            .AddScoped<IAgentAuditService, AgentAuditService>()
            .AddMediatR(configuration =>
            {
                configuration.RegisterServicesFromAssemblyContaining<ManageFeatureFlagsCommand>();
                configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
            })
            .AddSingleton<IValidator<ManageFeatureFlagsCommand>, ManageFeatureFlagsCommandValidator>()
            .AddSingleton<IFeatureFlagManagementService>(new FeatureFlagManagementService(context, resolver, NullLogger<FeatureFlagManagementService>.Instance))
            .BuildServiceProvider();
        var adapter = new ManageFeatureFlagsTool(provider.GetRequiredService<IMediator>());
        var catalog = new AgentCatalogService([adapter]);
        var settings = Options.Create(new AgentPlatformSettings());
        var pending = new PendingAgentOperationStore(context, settings);
        var stepUp = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUp.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentConfirmationRequirement?)null);
        var evaluator = new AgentPolicyEvaluator(context, catalog, pending, stepUp, settings, resolver);
        var executor = new AgentOperationExecutor(catalog, evaluator, provider.GetRequiredService<IAgentAuditService>(),
            new AgentTargetOwnershipService(context), stepUp, new AiToolRegistry([adapter]),
            Substitute.For<IPendingOperationChangePreviewer>(), pending,
            new UnitOfWork(context, new DatabaseConnectionSettings()), NullLogger<AgentOperationExecutor>.Instance);
        var tool = new FeatureFlagTools(provider.GetRequiredService<IMediator>(), new McpExecutorBridge(executor), provider);
        var principal = await AuthenticateAsync(admin.Id, oauthFamily);
        principal.GetAgentAuthMethod().Should().Be(oauthFamily ? AgentAuthMethod.OAuth : AgentAuthMethod.ApiKey);
        principal.GetGrantedAgentScopes().Should().NotContain(AgentScopes.ManageFeatureFlags);
        var gateway = new AgentTools(catalog, executor, pending, Substitute.For<IAgentStepUpService>());
        var nonAdmin = await AuthenticateAsync(account.Id, oauthFamily, [AgentScopes.ManageFeatureFlags]);
        await AssertDeniedAsync(nonAdmin, tool, gateway, provider);
        await AssertDiscoveryAsync(principal, tool, provider, true);
        (await ListAsync(tool, principal)).Single(flag => flag.Key == "astra_eval_trace").Accounts.Should().BeEmpty();

        var output = await tool.ManageFeatureFlags(principal, FeatureFlagActions.EnableForAccounts, "astra_eval_trace", ["ACCOUNT@example.com"]);

        output.Should().Contain("account@example.com");
        (await resolver.GetEnabledKeysForUserAsync(account.Id)).Should().Contain("astra_eval_trace");
        (await resolver.GetEnabledKeysForUserAsync(admin.Id)).Should().NotContain("astra_eval_trace");
        (await ListAsync(tool, principal)).Single(flag => flag.Key == "astra_eval_trace").Accounts.Should().Equal(account.Email);
        (await context.AgentAuditLogs.SingleAsync(log => log.OutcomeStatus == AgentOperationStatus.Succeeded))
            .UserId.Should().Be(admin.Id);
        await tool.ManageFeatureFlags(principal, FeatureFlagActions.DisableForAccounts, "astra_eval_trace", ["account@example.com"]);
        (await resolver.GetEnabledKeysForUserAsync(account.Id)).Should().NotContain("astra_eval_trace");

        (await ListAsync(tool, principal)).Single(flag => flag.Key == "astra_eval_trace").Accounts.Should().BeEmpty();

        (await tool.ManageFeatureFlags(principal, FeatureFlagActions.Create, "admin_test", description: "Admin test"))
            .Should().Contain("admin_test");
        await tool.ManageFeatureFlags(principal, FeatureFlagActions.EnableForEveryone, "admin_test");
        (await ListAsync(tool, principal)).Single(flag => flag.Key == "admin_test").EnabledForEveryone.Should().BeTrue();
        await tool.ManageFeatureFlags(principal, FeatureFlagActions.DisableForEveryone, "admin_test");
        (await ListAsync(tool, principal)).Single(flag => flag.Key == "admin_test").EnabledForEveryone.Should().BeFalse();
        var operation = await gateway.ExecuteAgentOperation(principal, FeatureFlagActions.ToolName,
            JsonSerializer.SerializeToElement(new { action = FeatureFlagActions.EnableForAccounts, key = "admin_test", emails = new[] { account.Email } }));
        operation.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);

        var readOnly = await AuthenticateAsync(admin.Id, oauthFamily, isReadOnly: true);
        foreach (var action in FeatureFlagActions.Writes)
        {
            (await tool.ManageFeatureFlags(readOnly, action, "astra_eval_trace", [account.Email], "Description"))
                .Should().Be("Error: read_only_credential");
            var denied = await gateway.ExecuteAgentOperation(readOnly, FeatureFlagActions.ToolName,
                JsonSerializer.SerializeToElement(new { action, key = "astra_eval_trace", emails = new[] { account.Email }, description = "Description" }));
            denied.Operation.PolicyReason.Should().Be("read_only_credential");
        }
        if (!oauthFamily)
        {
            gateway.ConfirmAgentOperation(principal, Guid.NewGuid().ToString()).IsError.Should().BeTrue();
            var missingScope = evaluator.Evaluate(new AgentPolicyEvaluationContext(AgentCapabilityIds.HabitsRead,
                admin.Id, AgentExecutionSurface.Mcp, AgentAuthMethod.ApiKey, [], "list_habits", "List habits"));
            missingScope.Reason.Should().Be($"missing_scope:{AgentScopes.ReadHabits}");
        }

        using (var externalContext = factory.CreateContext())
            await externalContext.Users.Where(user => user.Id == admin.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsAdmin, false));
        admin.IsAdmin.Should().BeTrue();
        await AssertDeniedAsync(principal, tool, gateway, provider);
        (await context.AppFeatureFlags.SingleAsync(flag => flag.Key == "astra_eval_trace")).Enabled.Should().BeFalse();
    }

    private static async Task<ClaimsPrincipal> AuthenticateAsync(Guid userId, bool oauthFamily,
        IReadOnlyList<string>? scopes = null, bool isReadOnly = false)
    {
        var (key, rawKey) = ApiKey.Create(userId, oauthFamily ? ApiKey.ConnectorName : "Manual key", scopes ?? AgentScopes.ClaudeDefaultScopes,
            isReadOnly: isReadOnly, oauthFamilyId: oauthFamily ? Guid.NewGuid() : null).Value;
        var run = await ApiKeyAuthenticationHandlerTests.RunHandler($"Bearer {rawKey}", candidates: [key]);
        run.Result.Succeeded.Should().BeTrue();
        return run.Result.Principal!;
    }

    private static async Task<IReadOnlyList<AdminFeatureFlag>> ListAsync(FeatureFlagTools tool, ClaimsPrincipal principal) =>
        JsonSerializer.Deserialize<List<AdminFeatureFlag>>(await tool.ManageFeatureFlags(principal, FeatureFlagActions.List))!;

    private static async Task AssertDeniedAsync(ClaimsPrincipal principal, FeatureFlagTools tool,
        AgentTools gateway, ServiceProvider provider)
    {
        await AssertDiscoveryAsync(principal, tool, provider, false);
        foreach (var action in FeatureFlagActions.Writes.Prepend(FeatureFlagActions.List))
        {
            var call = () => tool.ManageFeatureFlags(principal, action, "astra_eval_trace", ["account@example.com"], "Description");
            await call.Should().ThrowAsync<McpProtocolException>().WithMessage("Unknown tool: 'manage_feature_flags'");
            var operation = await gateway.ExecuteAgentOperation(principal, FeatureFlagActions.ToolName,
                JsonSerializer.SerializeToElement(new { action, key = "astra_eval_trace", emails = new[] { "account@example.com" }, description = "Description" }));
            operation.Operation.Status.Should().Be(AgentOperationStatus.Denied);
            operation.Operation.PolicyReason.Should().Be("tool_not_found");
        }
    }

    private static async Task AssertDiscoveryAsync(ClaimsPrincipal principal, FeatureFlagTools tool,
        ServiceProvider provider, bool visible)
    {
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var server = Substitute.For<McpServer>();
        var protocolTool = McpServerTool.Create(typeof(FeatureFlagTools).GetMethod(nameof(FeatureFlagTools.ManageFeatureFlags))!, tool);
        var listContext = new RequestContext<ListToolsRequestParams>(server,
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/list" }, new ListToolsRequestParams())
        { User = principal, Services = provider };
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> listHandler = (_, _) =>
            ValueTask.FromResult(new ListToolsResult { Tools = [protocolTool.ProtocolTool] });
        foreach (var filter in options.Filters.Request.ListToolsFilters.Reverse())
            listHandler = filter(listHandler);
        (await listHandler(listContext, CancellationToken.None)).Tools.Any(item => item.Name == FeatureFlagActions.ToolName)
            .Should().Be(visible);

        var callContext = new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Id = new RequestId(2), Method = "tools/call" }, new CallToolRequestParams { Name = FeatureFlagActions.ToolName })
        { User = principal, Services = provider };
        McpRequestHandler<CallToolRequestParams, CallToolResult> callHandler = (_, _) => ValueTask.FromResult(new CallToolResult());
        foreach (var filter in options.Filters.Request.CallToolFilters.Reverse())
            callHandler = filter(callHandler);
        if (visible)
            await callHandler(callContext, CancellationToken.None);
        else
        {
            var call = async () => await callHandler(callContext, CancellationToken.None);
            await call.Should().ThrowAsync<McpProtocolException>().WithMessage("Unknown tool: 'manage_feature_flags'");
        }
    }
}
