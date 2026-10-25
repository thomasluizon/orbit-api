using System.Linq.Expressions;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Api.OAuth;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Result = Orbit.Domain.Common.Result;

namespace Orbit.Infrastructure.Tests.Middleware;

public class McpPlanAccessTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ToolCall_RequiresProAccess_ForOAuthAndApiKeys(bool throughOAuth, bool hasProAccess)
    {
        var user = User.Create("MCP member", "member@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        if (hasProAccess)
            user.SetStripeSubscription("sub_mcp", DateTime.UtcNow.AddDays(30));
        user.HasProAccess.Should().Be(hasProAccess);

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PaginatedResponse<HabitScheduleItem>([], 1, 50, 0, 0)));
        var keys = new List<ApiKey>();
        var keyRepository = Substitute.For<IGenericRepository<ApiKey>>();
        keyRepository.AddAsync(Arg.Do<ApiKey>(keys.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        keyRepository.FindTrackedAsync(
                Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => keys.Where(call.Arg<Expression<Func<ApiKey, bool>>>().Compile()).ToList());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<IActionResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<IActionResult>>>()(call.Arg<CancellationToken>()));

        string token;
        if (throughOAuth)
        {
            token = await ExchangeOAuthTokenAsync(user, mediator, keyRepository, unitOfWork);
        }
        else
        {
            var created = ApiKey.Create(user.Id, "MCP client", [AgentScopes.ReadHabits]).Value;
            keys.Add(created.Entity);
            token = created.RawKey;
        }

        var userRepository = Substitute.For<IGenericRepository<User>>();
        userRepository.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var payGate = new PayGateService(
            Substitute.For<IGenericRepository<Habit>>(), userRepository,
            Substitute.For<IAppConfigService>(), Substitute.For<IUserDateService>());
        var catalog = new AgentCatalogService();
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Allowed, catalog.GetCapabilityByMcpTool("list_habits")));
        var rateLimiter = Substitute.For<IDistributedRateLimitService>();
        rateLimiter.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));

        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Jwt:SecretKey"] = new string('s', 64);
        builder.Configuration["Jwt:Issuer"] = "orbit-test";
        builder.Configuration["Jwt:Audience"] = "orbit-test";
        builder.AddOrbitAuthentication();
        var usage = Substitute.For<IApiKeyUsageTracker>();
        builder.Services.AddSingleton(usage);
        builder.Services.AddSingleton(Orbit.Infrastructure.Tests.ModerationTestDouble.Clean());
        builder.Services.AddSingleton(keyRepository);
        builder.Services.AddSingleton(unitOfWork);
        builder.Services.AddSingleton<IPayGateService>(payGate);
        builder.Services.AddSingleton<IAgentCatalogService>(catalog);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton(rateLimiter);
        builder.Services.AddSingleton(Substitute.For<IAgentAuditService>());
        builder.Services.AddMemoryCache();
        await using var services = builder.Services.BuildServiceProvider();

        var tools = new HabitTools(mediator, Substitute.For<IUserDateService>(),
            new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>()));
        var pipeline = new ApplicationBuilder(services);
        pipeline.UseMcpSelectiveAuth();
        pipeline.UseAuthentication();
        pipeline.UseAuthorization();
        string? toolOutput = null;
        pipeline.Run(async context =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var parameters = body.RootElement.GetProperty("params");
            parameters.GetProperty("name").GetString().Should().Be("list_habits");
            var arguments = parameters.GetProperty("arguments");
            toolOutput = await tools.ListHabits(context.User,
                arguments.GetProperty("dateFrom").GetString()!,
                arguments.GetProperty("dateTo").GetString()!);
        });
        var request = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(new CallToolRequestParams
            {
                Name = "list_habits",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["dateFrom"] = JsonSerializer.SerializeToElement("2026-01-01"),
                    ["dateTo"] = JsonSerializer.SerializeToElement("2026-01-07")
                }
            }, McpJsonUtilities.DefaultOptions)
        };
        var requestBody = JsonSerializer.Serialize<JsonRpcMessage>(request, McpJsonUtilities.DefaultOptions);
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.Request.Headers.Authorization = $"Bearer {token}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));

        await pipeline.Build()(context);

        var key = keys.Should().ContainSingle().Subject;
        if (hasProAccess)
        {
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.User.Identity!.IsAuthenticated.Should().BeTrue();
            context.User.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
            context.User.FindFirstValue("auth_method").Should().Be(throughOAuth ? "oauth" : "api_key");
            toolOutput.Should().Be("No habits found for the given date range.");
            await usage.Received(1).MarkUsedAsync(key.Id, Arg.Any<CancellationToken>());
            await mediator.Received(1).Send(
                Arg.Is<GetHabitScheduleQuery>(query => query.UserId == user.Id), Arg.Any<CancellationToken>());
        }
        else
        {
            context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
            context.Response.Headers.WWWAuthenticate.ToString().Should().Be(
                "Bearer resource_metadata=\"https://api.example.com/.well-known/oauth-protected-resource/mcp\"");
            toolOutput.Should().BeNull();
            key.LastUsedAtUtc.Should().BeNull();
            await mediator.DidNotReceive().Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>());
            await rateLimiter.DidNotReceive().TryAcquireAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        await userRepository.Received().GetByIdAsync(user.Id, Arg.Any<CancellationToken>());
    }

    private static async Task<string> ExchangeOAuthTokenAsync(
        User user, IMediator mediator, IGenericRepository<ApiKey> keys, IUnitOfWork unitOfWork)
    {
        using var oauthDb = new Orbit.Infrastructure.Tests.Persistence.SqliteOrbitDbContextFactory();
        var store = new OAuthAuthorizationStore(new Orbit.Infrastructure.Persistence.OAuthStateStore(oauthDb.Context, TimeProvider.System), TimeProvider.System);
        mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(user.Id, "login-token", user.Name, user.Email)));
        var controller = new OAuthController(mediator, store, keys, unitOfWork,
            Options.Create(new GoogleSettings()), new ConfigurationBuilder().Build(),
            NullLogger<OAuthController>.Instance, Substitute.For<IOAuthClientMetadataService>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        const string verifier = "mcp-client-code-verifier";
        const string redirectUri = "https://claude.ai/callback";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var login = await controller.VerifyCode(new OAuthController.VerifyCodeRequest(
            user.Email, "123456", "client-state", challenge, redirectUri, "mcp-client"), CancellationToken.None);
        var redirect = JsonSerializer.SerializeToElement(login.Should().BeOfType<OkObjectResult>().Subject.Value)
            .GetProperty("redirectUrl").GetString()!;
        var ticket = QueryHelpers.ParseQuery(new Uri("https://api.useorbit.org" + redirect).Query)["ticket"].ToString();
        redirect = ((RedirectResult)controller.GrantConsent(ticket, "requested")).Url;
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        var result = await controller.Token("authorization_code", code, verifier, redirectUri, CancellationToken.None, client_id: "mcp-client");

        var response = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        response.GetProperty("token_type").GetString().Should().Be("Bearer");
        return response.GetProperty("access_token").GetString()!;
    }
}
