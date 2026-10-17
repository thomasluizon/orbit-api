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
    [Fact]
    public async Task McpWrite_UsesPolicyValidationPersistenceInvalidationAndAudit()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new FeatureFlagService(context, cache);
        var admin = User.Create("Admin", "admin@example.com").Value;
        admin.GrantAdmin();
        var account = User.Create("Account", "account@example.com").Value;
        context.Users.AddRange(admin, account);
        await context.SaveChangesAsync();
        (await resolver.GetEnabledKeysForUserAsync(account.Id)).Should().NotContain("redesign");
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AdminPolicy.Name).Returns(AuthorizationResult.Success());
        using var provider = new ServiceCollection().AddLogging()
            .AddMediatR(configuration =>
            {
                configuration.RegisterServicesFromAssemblyContaining<ManageFeatureFlagsCommand>();
                configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
            })
            .AddSingleton<IValidator<ManageFeatureFlagsCommand>, ManageFeatureFlagsCommandValidator>()
            .AddSingleton<IFeatureFlagManagementService>(new FeatureFlagManagementService(context, resolver, NullLogger<FeatureFlagManagementService>.Instance))
            .AddSingleton(authorization)
            .BuildServiceProvider();
        var adapter = new ManageFeatureFlagsTool(provider.GetRequiredService<IMediator>());
        var catalog = new AgentCatalogService([adapter]);
        var settings = Options.Create(new AgentPlatformSettings());
        var pending = new PendingAgentOperationStore(context, settings);
        var stepUp = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUp.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentConfirmationRequirement?)null);
        var evaluator = new AgentPolicyEvaluator(context, catalog, pending, stepUp, settings, resolver);
        var executor = new AgentOperationExecutor(catalog, evaluator, new AgentAuditService(context),
            new AgentTargetOwnershipService(context), stepUp, new AiToolRegistry([adapter]),
            new UnitOfWork(context, new DatabaseConnectionSettings()), NullLogger<AgentOperationExecutor>.Instance);
        var tool = new FeatureFlagTools(provider.GetRequiredService<IMediator>(), new McpExecutorBridge(executor), provider);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString())], "Test"));

        var output = await tool.ManageFeatureFlags(principal, FeatureFlagActions.EnableForAccounts, "redesign", ["ACCOUNT@example.com"]);

        output.Should().Contain("account@example.com");
        (await resolver.GetEnabledKeysForUserAsync(account.Id)).Should().Contain("redesign");
        (await resolver.GetEnabledKeysForUserAsync(admin.Id)).Should().NotContain("redesign");
        (await context.AgentAuditLogs.SingleAsync()).UserId.Should().Be(admin.Id);
        await tool.ManageFeatureFlags(principal, FeatureFlagActions.DisableForAccounts, "redesign", ["account@example.com"]);
        (await resolver.GetEnabledKeysForUserAsync(account.Id)).Should().NotContain("redesign");

        var readOnly = await executor.ExecuteAsync(new AgentExecuteOperationRequest(admin.Id, FeatureFlagActions.ToolName,
            JsonSerializer.SerializeToElement(new { action = "enable_for_everyone", key = "redesign" }),
            AgentExecutionSurface.Mcp, AgentAuthMethod.ApiKey, [AgentScopes.ManageFeatureFlags], IsReadOnlyCredential: true));
        readOnly.Operation.PolicyReason.Should().Be("read_only_credential");
        var nonAdmin = await executor.ExecuteAsync(new AgentExecuteOperationRequest(account.Id, FeatureFlagActions.ToolName,
            JsonSerializer.SerializeToElement(new { action = "enable_for_everyone", key = "redesign" }), AgentExecutionSurface.Mcp, AgentAuthMethod.Jwt));
        nonAdmin.Operation.PolicyReason.Should().Be("tool_not_found");
        (await context.AppFeatureFlags.SingleAsync(flag => flag.Key == "redesign")).Enabled.Should().BeFalse();
    }
}
