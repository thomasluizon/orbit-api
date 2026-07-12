using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Extensions;

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
}
