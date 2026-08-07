using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Extensions;

/// <summary>
/// The MCP selective-auth pre-parse helpers. The middleware buffers the body
/// and parses it once; both the unauthenticated-method probe and the tool-call
/// extractor read that single parsed document. These tests pin the
/// classification behavior and prove non-object / malformed bodies degrade to a
/// pass-through instead of throwing.
/// </summary>
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
        using var document = WebApplicationExtensions.TryParseMcpBody(
            "{\"method\":\"tools/call\",\"id\":42,\"params\":{\"name\":\"get_habits\",\"arguments\":{\"a\":1}}}");

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
        using var document = WebApplicationExtensions.TryParseMcpBody(
            "{\"method\":\"tools/call\",\"params\":{\"name\":\"execute_agent_operation_v2\",\"operationId\":\"op-7\",\"arguments\":{\"x\":true}}}");

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
        using (var document = WebApplicationExtensions.TryParseMcpBody(
            "{\"method\":\"tools/call\",\"id\":\"abc\",\"params\":{\"name\":\"get_habits\"}}"))
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
        using var requestDocument = WebApplicationExtensions.TryParseMcpBody(
            "{\"jsonrpc\":\"2.0\",\"id\":\"list-7\",\"method\":\"tools/list\"}");
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
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(context, toolName, requestId))
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
            requestId: null)).Should().BeTrue();
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            CreateRateLimitContext(service, firstKey),
            "get_habits",
            requestId: null)).Should().BeFalse();
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            CreateRateLimitContext(service, secondKey),
            "get_habits",
            requestId: null)).Should().BeTrue();
    }

    [Theory]
    [InlineData("get_daily_summary")]
    [InlineData("get_goal_review")]
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
            requestIdDocument.RootElement.Clone())).Should().BeTrue();
        (await WebApplicationExtensions.TryApplyMcpRateLimitsAsync(
            rejectedContext,
            toolName,
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
        Guid? userId = null)
    {
        var services = new ServiceCollection()
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
}
