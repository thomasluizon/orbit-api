using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Extensions;

public class WebApplicationExtensionsMcpTests
{
    [Theory]
    [InlineData("{\"method\":\"initialize\"}")]
    [InlineData("{\"method\":\"ping\"}")]
    [InlineData("{\"method\":\"notifications/cancelled\"}")]
    [InlineData("{\"method\":\"notifications/initialized\"}")]
    public void IsMcpUnauthenticatedMethod_AllowsHandshakeAndNotifications(string body)
    {
        using var document = WebApplicationExtensions.TryParseMcpBody(body);

        WebApplicationExtensions.IsMcpUnauthenticatedMethod(document?.RootElement).Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"method\":\"tools/call\"}")]
    [InlineData("{\"method\":\"tools/list\"}")]
    [InlineData("{\"id\":1}")]
    public void IsMcpUnauthenticatedMethod_RequiresAuthForEverythingElse(string body)
    {
        using var document = WebApplicationExtensions.TryParseMcpBody(body);

        WebApplicationExtensions.IsMcpUnauthenticatedMethod(document?.RootElement).Should().BeFalse();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"initialize\"")]
    [InlineData("[{\"method\":\"initialize\"}]")]
    [InlineData("true")]
    [InlineData("null")]
    public void IsMcpUnauthenticatedMethod_NonObjectBody_ReturnsFalseWithoutThrowing(string body)
    {
        using var document = WebApplicationExtensions.TryParseMcpBody(body);

        var act = () => WebApplicationExtensions.IsMcpUnauthenticatedMethod(document?.RootElement);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void IsMcpUnauthenticatedMethod_MalformedJson_ReturnsFalse()
    {
        using var document = WebApplicationExtensions.TryParseMcpBody("{ not valid json");

        document.Should().BeNull();
        WebApplicationExtensions.IsMcpUnauthenticatedMethod(document?.RootElement).Should().BeFalse();
    }

    [Fact]
    public void TryGetMcpToolCall_ValidCall_ExtractsToolNameIdAndFingerprint()
    {
        using var document = CreateSdkToolCallDocument(
            "get_habits",
            new RequestId(42),
            new Dictionary<string, JsonElement>
            {
                ["a"] = JsonSerializer.SerializeToElement(1)
            });
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("jsonrpc", "id", "method", "params");
        document.RootElement.GetProperty("params").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("name", "arguments");

        var matched = WebApplicationExtensions.TryGetMcpToolCall(
            document?.RootElement,
            out var toolName,
            out var requestId,
            out var operationId,
            out var operationFingerprint);

        matched.Should().BeTrue();
        toolName.Should().Be("get_habits");
        requestId.Should().NotBeNull();
        requestId!.Value.GetInt32().Should().Be(42);
        operationId.Should().BeNull();
        operationFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryGetMcpToolCall_AgentOperation_CapturesOperationId()
    {
        using var document = CreateSdkToolCallDocument(
            "execute_agent_operation_v2",
            new RequestId(7),
            new Dictionary<string, JsonElement>
            {
                ["operationId"] = JsonSerializer.SerializeToElement("op-7"),
                ["arguments"] = JsonSerializer.SerializeToElement(new { x = true })
            });

        var matched = WebApplicationExtensions.TryGetMcpToolCall(
            document?.RootElement,
            out var toolName,
            out _,
            out var operationId,
            out var operationFingerprint);

        matched.Should().BeTrue();
        toolName.Should().Be("execute_agent_operation_v2");
        operationId.Should().Be("op-7");
        operationFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryGetMcpToolCall_ClonedRequestId_SurvivesDocumentDisposal()
    {
        JsonElement? requestId;
        using (var document = CreateSdkToolCallDocument("get_habits", new RequestId("abc")))
        {
            WebApplicationExtensions.TryGetMcpToolCall(
                document?.RootElement, out _, out requestId, out _, out _);
        }

        requestId.Should().NotBeNull();
        requestId!.Value.GetString().Should().Be("abc");
    }

    [Theory]
    [InlineData("{\"method\":\"tools/list\"}")]
    [InlineData("{\"method\":\"tools/call\"}")]
    [InlineData("{\"method\":\"tools/call\",\"params\":{}}")]
    [InlineData("123")]
    [InlineData("[1,2,3]")]
    public void TryGetMcpToolCall_NonToolCallOrNonObject_ReturnsFalseWithoutThrowing(string body)
    {
        using var document = WebApplicationExtensions.TryParseMcpBody(body);

        var act = () => WebApplicationExtensions.TryGetMcpToolCall(
            document?.RootElement, out _, out _, out _, out _);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void TryGetMcpToolCall_MalformedJson_ReturnsFalse()
    {
        using var document = WebApplicationExtensions.TryParseMcpBody("}{");

        document.Should().BeNull();
        WebApplicationExtensions.TryGetMcpToolCall(
            document?.RootElement, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryApplyMcpRateLimitsAsync_NonToolRequest_PreservesRequestIdInRejection()
    {
        using var requestDocument = CreateSdkRequestDocument(RequestMethods.ToolsList, new RequestId("list-7"));
        var isToolCall = WebApplicationExtensions.TryGetMcpToolCall(
            requestDocument?.RootElement,
            out var toolName,
            out var requestId,
            out _,
            out _);
        var service = Substitute.For<IDistributedRateLimitService>();
        service.TryAcquireAsync(
                WebApplicationExtensions.McpRateLimitPolicy,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 60, 60, DateTime.UtcNow.AddMinutes(1)));
        var context = CreateRateLimitContext(service, Guid.NewGuid());

        isToolCall.Should().BeFalse();
        toolName.Should().BeNull();
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            context,
            toolName,
            operationId: null,
            requestId))
            .Should().BeFalse();

        context.Response.Body.Position = 0;
        using var responseDocument = await JsonDocument.ParseAsync(context.Response.Body);
        responseDocument.RootElement.GetProperty("id").GetString().Should().Be("list-7");
    }

    [Fact]
    public async Task TryApplyMcpRateLimitsAsync_ExhaustedKey_DoesNotAffectSecondKey()
    {
        var service = Substitute.For<IDistributedRateLimitService>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        service.TryAcquireAsync(
                WebApplicationExtensions.McpRateLimitPolicy,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var partitionKey = call.ArgAt<string>(1);
                counts.TryGetValue(partitionKey, out var count);
                count++;
                counts[partitionKey] = count;
                return new DistributedRateLimitDecision(
                    count <= 1,
                    1,
                    Math.Min(count, 1),
                    DateTime.UtcNow.AddMinutes(1));
            });

        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();

        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            CreateRateLimitContext(service, firstKey),
            "get_habits",
            operationId: null,
            requestId: null)).Should().BeTrue();
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            CreateRateLimitContext(service, firstKey),
            "get_habits",
            operationId: null,
            requestId: null)).Should().BeFalse();
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            CreateRateLimitContext(service, secondKey),
            "get_habits",
            operationId: null,
            requestId: null)).Should().BeTrue();
    }

    [Theory]
    [InlineData(WebApplicationExtensions.McpRateLimitPolicy)]
    [InlineData(WebApplicationExtensions.McpAiRateLimitPolicy)]
    public async Task UseMcpSelectiveAuth_ExhaustedKey_RejectsBeforeNextAndLeavesSecondKeyUnaffected(string exhaustedPolicy)
    {
        var limiter = Substitute.For<IDistributedRateLimitService>();
        var counts = new Dictionary<(string Policy, string Partition), int>();
        limiter.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var policy = call.ArgAt<string>(0);
                var partition = call.ArgAt<string>(1);
                counts.TryGetValue((policy, partition), out var count);
                counts[(policy, partition)] = ++count;
                var limit = policy == exhaustedPolicy ? 1 : 60;
                return new DistributedRateLimitDecision(count <= limit, limit, Math.Min(count, limit), DateTime.UtcNow.AddMinutes(1));
            });
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(call => AuthenticateResult.Success(new AuthenticationTicket(call.Arg<HttpContext>().User, "ApiKey")));
        var moderation = ModerationTestDouble.Clean();
        var catalog = new AgentCatalogService();
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, catalog.GetCapabilityByMcpTool("get_daily_summary")));
        await using var services = new ServiceCollection()
            .AddSingleton(moderation)
            .AddSingleton(limiter)
            .AddSingleton(authentication)
            .AddSingleton<IAgentCatalogService>(catalog)
            .AddSingleton(policy)
            .AddSingleton(Substitute.For<IAgentAuditService>())
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        var nextRequests = new List<HttpContext>();
        var pipeline = new ApplicationBuilder(services);
        pipeline.UseMcpSelectiveAuth();
        pipeline.Run(context =>
        {
            context.User.Identity!.IsAuthenticated.Should().BeTrue();
            context.Request.Body.Position.Should().Be(0);
            nextRequests.Add(context);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var invoke = pipeline.Build();
        var userId = Guid.NewGuid();
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();
        using var document = CreateSdkToolCallDocument("get_daily_summary", new RequestId(42));
        var body = Encoding.UTF8.GetBytes(document.RootElement.GetRawText());

        DefaultHttpContext Request(Guid apiKeyId)
        {
            var context = CreateRateLimitContext(limiter, apiKeyId, userId, services);
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/mcp";
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(body);
            return context;
        }

        var allowed = Request(firstKey);
        await invoke(allowed);
        allowed.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        nextRequests.Should().Equal(allowed);

        var limited = Request(firstKey);
        await invoke(limited);
        limited.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        nextRequests.Should().Equal(allowed);
        limited.Response.Body.Position = 0;
        using var rejection = await JsonDocument.ParseAsync(limited.Response.Body);
        rejection.RootElement.GetProperty("id").GetInt32().Should().Be(42);
        rejection.RootElement.GetProperty("error").GetProperty("data").GetProperty("policy").GetString()
            .Should().Be(exhaustedPolicy);

        var otherKey = Request(secondKey);
        await invoke(otherKey);
        otherKey.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        nextRequests.Should().Equal(allowed, otherKey);
        counts[(WebApplicationExtensions.McpRateLimitPolicy, $"api-key:{firstKey}")].Should().Be(2);
        counts[(WebApplicationExtensions.McpAiRateLimitPolicy, $"api-key:{firstKey}")]
            .Should().Be(exhaustedPolicy == WebApplicationExtensions.McpRateLimitPolicy ? 1 : 2);
        counts[(WebApplicationExtensions.McpRateLimitPolicy, $"api-key:{secondKey}")].Should().Be(1);
        counts[(WebApplicationExtensions.McpAiRateLimitPolicy, $"api-key:{secondKey}")].Should().Be(1);
        await authentication.Received(3).AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>());
        await moderation.Received(2).CheckTextAsync(Encoding.UTF8.GetString(body), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfigureOrbitPipeline_LimitedMcpPost_RejectsBeforeNext()
    {
        var limiter = Substitute.For<IDistributedRateLimitService>();
        limiter.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 60, 60, DateTime.UtcNow.AddMinutes(1)));
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(call => AuthenticateResult.Success(new AuthenticationTicket(call.Arg<HttpContext>().User, "ApiKey")));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration["Database:MigrateOnStartup"] = "false";
        builder.Services.AddResponseCompression();
        builder.Services.AddExceptionHandler(options => options.ExceptionHandler = _ => Task.CompletedTask);
        builder.Services.AddCors(options => options.AddPolicy("ThirdParty", policy => policy.WithOrigins("https://client.example.com")));
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddControllers();
        builder.Services.AddMcpServer().WithHttpTransport();
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(limiter);
        builder.Services.AddSingleton(authentication);
        builder.Services.AddSingleton(Substitute.For<IAppConfigService>());
        await using var app = builder.Build();
        await app.ConfigureOrbitPipeline();
        var nextCalled = false;
        app.Run(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var invoke = ((IApplicationBuilder)app).Build();
        var apiKeyId = Guid.NewGuid();
        var context = CreateRateLimitContext(limiter, apiKeyId, Guid.NewGuid(), app.Services);
        using var document = CreateSdkToolCallDocument("get_daily_summary", new RequestId(42));
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(document.RootElement.GetRawText()));

        await invoke(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        nextCalled.Should().BeFalse();
        await authentication.Received(1).AuthenticateAsync(context, Arg.Any<string?>());
        await limiter.Received(1).TryAcquireAsync(
            WebApplicationExtensions.McpRateLimitPolicy, $"api-key:{apiKeyId}", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("get_daily_summary")]
    [InlineData("get_goal_review")]
    [InlineData("get_retrospective")]
    public async Task TryApplyMcpRateLimitsAsync_AiBearingTool_HitsAiLimitBeforeGeneralLimit(string toolName)
    {
        var service = Substitute.For<IDistributedRateLimitService>();
        var aiCount = 0;
        service.TryAcquireAsync(
                WebApplicationExtensions.McpRateLimitPolicy,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));
        service.TryAcquireAsync(
                WebApplicationExtensions.McpAiRateLimitPolicy,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                aiCount++;
                return new DistributedRateLimitDecision(
                    aiCount <= 1,
                    1,
                    Math.Min(aiCount, 1),
                    DateTime.UtcNow.AddMinutes(1));
            });

        var apiKeyId = Guid.NewGuid();
        var allowedContext = CreateRateLimitContext(service, apiKeyId);
        var rejectedContext = CreateRateLimitContext(service, apiKeyId);
        using var requestIdDocument = JsonDocument.Parse("42");

        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            allowedContext,
            toolName,
            operationId: null,
            requestIdDocument.RootElement.Clone())).Should().BeTrue();
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            rejectedContext,
            toolName,
            operationId: null,
            requestIdDocument.RootElement.Clone())).Should().BeFalse();

        rejectedContext.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        rejectedContext.Response.Headers.RetryAfter.Should().NotBeEmpty();
        rejectedContext.Response.Body.Position = 0;
        using var responseDocument = await JsonDocument.ParseAsync(rejectedContext.Response.Body);
        var error = responseDocument.RootElement.GetProperty("error");
        error.GetProperty("message").GetString().Should().Be("rate_limit_exceeded");
        error.GetProperty("data").GetProperty("policy").GetString()
            .Should().Be(WebApplicationExtensions.McpAiRateLimitPolicy);

        await service.Received(2).TryAcquireAsync(
            WebApplicationExtensions.McpRateLimitPolicy,
            $"api-key:{apiKeyId}",
            Arg.Any<CancellationToken>());
        await service.Received(2).TryAcquireAsync(
            WebApplicationExtensions.McpAiRateLimitPolicy,
            $"api-key:{apiKeyId}",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("get_daily_summary")]
    [InlineData("get_retrospective")]
    [InlineData("review_goals")]
    public async Task TryApplyMcpRateLimitsAsync_WrappedAiOperation_UsesAiPolicy(string operationId)
    {
        using var document = CreateSdkToolCallDocument(
            "execute_agent_operation_v2",
            new RequestId(9),
            new Dictionary<string, JsonElement>
            {
                ["operationId"] = JsonSerializer.SerializeToElement(operationId),
                ["arguments"] = JsonSerializer.SerializeToElement(new { })
            });
        WebApplicationExtensions.TryGetMcpToolCall(
            document.RootElement,
            out var toolName,
            out var requestId,
            out var extractedOperationId,
            out _).Should().BeTrue();
        var service = Substitute.For<IDistributedRateLimitService>();
        service.TryAcquireAsync(
                WebApplicationExtensions.McpRateLimitPolicy,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));
        service.TryAcquireAsync(
                WebApplicationExtensions.McpAiRateLimitPolicy,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 15, 15, DateTime.UtcNow.AddMinutes(1)));
        var apiKeyId = Guid.NewGuid();

        var allowed = await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            CreateRateLimitContext(service, apiKeyId),
            toolName,
            extractedOperationId,
            requestId);

        allowed.Should().BeFalse();
        extractedOperationId.Should().Be(operationId);
        await service.Received(1).TryAcquireAsync(
            WebApplicationExtensions.McpAiRateLimitPolicy,
            $"api-key:{apiKeyId}",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryApplyMcpRateLimitsAsync_JwtPrincipal_UsesUserPartition()
    {
        var service = Substitute.For<IDistributedRateLimitService>();
        service.TryAcquireAsync(
                WebApplicationExtensions.McpRateLimitPolicy,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));
        var userId = Guid.NewGuid();
        var context = CreateRateLimitContext(service, apiKeyId: null, userId);

        var allowed = await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            context,
            "get_habits",
            operationId: null,
            requestId: null);

        allowed.Should().BeTrue();
        await service.Received(1).TryAcquireAsync(
            WebApplicationExtensions.McpRateLimitPolicy,
            $"user:{userId}",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryApplyMcpRateLimitsAsync_UnauthenticatedRequest_DoesNotCallLimiter()
    {
        var service = Substitute.For<IDistributedRateLimitService>();
        var context = CreateRateLimitContext(service, apiKeyId: null);

        var allowed = await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            context,
            "get_daily_summary",
            operationId: null,
            requestId: null);

        allowed.Should().BeTrue();
        await service.DidNotReceive().TryAcquireAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private static DefaultHttpContext CreateRateLimitContext(
        IDistributedRateLimitService service,
        Guid? apiKeyId,
        Guid? userId = null,
        IServiceProvider? requestServices = null)
    {
        var services = requestServices ?? new ServiceCollection()
            .AddSingleton(service)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };

        if (apiKeyId.HasValue || userId.HasValue)
        {
            var claims = new List<Claim>();
            if (apiKeyId.HasValue)
                claims.Add(new Claim("api_key_id", apiKeyId.Value.ToString()));
            if (userId.HasValue)
                claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));

            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                apiKeyId.HasValue ? "ApiKey" : "JwtBearer"));
        }

        return context;
    }

    private static JsonDocument CreateSdkToolCallDocument(
        string toolName,
        RequestId requestId,
        IDictionary<string, JsonElement>? arguments = null)
    {
        var callParams = new CallToolRequestParams
        {
            Name = toolName,
            Arguments = arguments
        };

        return CreateSdkRequestDocument(
            RequestMethods.ToolsCall,
            requestId,
            JsonSerializer.SerializeToNode(callParams, McpJsonUtilities.DefaultOptions));
    }

    private static JsonDocument CreateSdkRequestDocument(
        string method,
        RequestId requestId,
        System.Text.Json.Nodes.JsonNode? parameters = null)
    {
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = method,
            Params = parameters
        };

        return JsonDocument.Parse(
            JsonSerializer.Serialize<JsonRpcMessage>(request, McpJsonUtilities.DefaultOptions));
    }
}
