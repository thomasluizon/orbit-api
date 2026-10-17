using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Queries;
using Result = Orbit.Domain.Common.Result;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

public class FeatureFlagToolsTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    private ClaimsPrincipal User => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"));

    [Fact]
    public async Task Discovery_AndDirectCall_HideToolFromNonAdmin()
    {
        using var services = BuildServices(false);
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var server = Substitute.For<McpServer>();
        var tool = McpServerTool.Create(typeof(FeatureFlagTools).GetMethod(nameof(FeatureFlagTools.ManageFeatureFlags))!,
            new FeatureFlagTools(_mediator, new McpExecutorBridge(_executor), services));
        var listContext = new RequestContext<ListToolsRequestParams>(server,
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/list" }, new ListToolsRequestParams())
        { User = User, Services = services };
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> listHandler = (_, _) =>
            ValueTask.FromResult(new ListToolsResult { Tools = [tool.ProtocolTool] });
        foreach (var filter in options.Filters.Request.ListToolsFilters.Reverse())
            listHandler = filter(listHandler);
        (await listHandler(listContext, CancellationToken.None)).Tools.Should().BeEmpty();

        var callContext = new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Id = new RequestId(2), Method = "tools/call" }, new CallToolRequestParams { Name = FeatureFlagActions.ToolName })
        { User = User, Services = services };
        var executed = false;
        McpRequestHandler<CallToolRequestParams, CallToolResult> callHandler = (_, _) =>
        {
            executed = true;
            return ValueTask.FromResult(new CallToolResult());
        };
        foreach (var filter in options.Filters.Request.CallToolFilters.Reverse())
            callHandler = filter(callHandler);
        var call = async () => await callHandler(callContext, CancellationToken.None);
        await call.Should().ThrowAsync<McpProtocolException>().WithMessage("Unknown tool: 'manage_feature_flags'");
        executed.Should().BeFalse();
        var direct = () => new FeatureFlagTools(_mediator, new McpExecutorBridge(_executor), services)
            .ManageFeatureFlags(User, "list");
        await direct.Should().ThrowAsync<McpProtocolException>();
        _executor.ReceivedCalls().Should().BeEmpty();
        _mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_CanDiscoverAndListFlags()
    {
        using var services = BuildServices(true);
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var tool = McpServerTool.Create(typeof(FeatureFlagTools).GetMethod(nameof(FeatureFlagTools.ManageFeatureFlags))!,
            new FeatureFlagTools(_mediator, new McpExecutorBridge(_executor), services));
        var context = new RequestContext<ListToolsRequestParams>(Substitute.For<McpServer>(),
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/list" }, new ListToolsRequestParams())
        { User = User, Services = services };
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> handler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [tool.ProtocolTool] });
        foreach (var filter in options.Filters.Request.ListToolsFilters.Reverse())
            handler = filter(handler);
        (await handler(context, CancellationToken.None)).Tools.Should().ContainSingle().Which.Name.Should().Be(FeatureFlagActions.ToolName);
        _mediator.Send(Arg.Is<ListFeatureFlagsQuery>(query => query.AdminId == _userId), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<AdminFeatureFlag>>([new("flag", "Description", false, null, ["account@example.com"])]));

        var result = await new FeatureFlagTools(_mediator, new McpExecutorBridge(_executor), services).ManageFeatureFlags(User, "list");

        result.Should().Contain("account@example.com").And.Contain("Description");
        _executor.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(FeatureFlagActions.Create)]
    [InlineData(FeatureFlagActions.EnableForAccounts)]
    [InlineData(FeatureFlagActions.DisableForAccounts)]
    [InlineData(FeatureFlagActions.EnableForEveryone)]
    [InlineData(FeatureFlagActions.DisableForEveryone)]
    public async Task Writes_RouteThroughExecutorWithCallerIdentity(string action)
    {
        using var services = BuildServices(true);
        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(FeatureFlagActions.ToolName, FeatureFlagActions.ToolName,
                AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, "Updated", Payload: new { key = "flag" })));

        await new FeatureFlagTools(_mediator, new McpExecutorBridge(_executor), services)
            .ManageFeatureFlags(User, action, "flag", ["account@example.com"], "Description");

        await _executor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.UserId == _userId && request.OperationId == FeatureFlagActions.ToolName &&
            request.Arguments.GetProperty("action").GetString() == action &&
            request.Arguments.GetProperty("key").GetString() == "flag"), Arg.Any<CancellationToken>());
        _mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void AdminOperation_IsExcludedFromChatAndPublicCatalogs()
    {
        var tool = new ManageFeatureFlagsTool(_mediator);
        var registry = new AiToolRegistry([tool]);
        registry.GetTool(FeatureFlagActions.ToolName).Should().BeSameAs(tool);
        registry.GetAll().Should().BeEmpty();
        var catalog = new AgentCatalogService([tool]);
        catalog.GetOperation(FeatureFlagActions.ToolName).Should().NotBeNull();
        catalog.GetOperations().Should().NotContain(operation => operation.Id == FeatureFlagActions.ToolName);
        catalog.GetCapabilities().Should().NotContain(capability => capability.Id == FeatureFlagActions.CapabilityId);
    }

    private ServiceProvider BuildServices(bool isAdmin)
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AdminPolicy.Name)
            .Returns(isAdmin ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var services = new ServiceCollection().AddLogging().AddSingleton(_authorization);
        services.AddMcpServer().WithTools<FeatureFlagTools>().WithFeatureFlagAccess();
        return services.BuildServiceProvider();
    }
}
