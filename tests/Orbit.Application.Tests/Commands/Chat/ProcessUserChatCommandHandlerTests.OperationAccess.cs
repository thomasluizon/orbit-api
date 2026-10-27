using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true, "read")]
    [InlineData(false, "write")]
    public async Task Handle_UnsupportedTool_PreservesKnownAccess(bool isReadOnly, string access)
    {
        SetupUserAndPayGate();
        SetupOperationAccessContinuation();
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns(isReadOnly ? "get_retrospective" : "create_tag");
        tool.Description.Returns("Requested operation");
        tool.IsReadOnly.Returns(isReadOnly);
        tool.GetParameterSchema().Returns(new { type = "object" });
        var handler = CreateHandler(tool);
        _catalogService.GetCapabilityByChatTool(tool.Name).Returns((AgentCapability?)null);
        SetupAiResponse(ToolResponse(tool.Name, "unsupported", "{}"));

        var result = await handler.Handle(new(UserId, "Run the requested operation"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var operation = result.Value.Operations.Should().ContainSingle().Subject;
        operation.Status.Should().Be(AgentOperationStatus.UnsupportedByPolicy);
        operation.Access.Should().Be(access);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, "read")]
    [InlineData(true, "write")]
    public async Task Handle_UnknownTool_UsesKnownCapabilityOrOmitsAccess(bool? isMutation, string? access)
    {
        SetupUserAndPayGate();
        SetupOperationAccessContinuation();
        var handler = CreateHandler();
        const string toolName = "unregistered_tool";
        _catalogService.GetCapabilityByChatTool(toolName)
            .Returns(isMutation is { } mutation ? BuildCapability(toolName) with { IsMutation = mutation } : null);
        SetupAiResponse(ToolResponse(toolName, "unknown", "{}"));

        var result = await handler.Handle(new(UserId, "Run the requested operation"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var operation = result.Value.Operations.Should().ContainSingle().Subject;
        operation.Status.Should().Be(AgentOperationStatus.UnsupportedByPolicy);
        operation.Access.Should().Be(access);
        using var json = JsonDocument.Parse(ChatStreamEvent.Final(result.Value).ToJson());
        var serialized = json.RootElement.GetProperty("response").GetProperty("operations")[0];
        serialized.TryGetProperty("access", out var value).Should().Be(access is not null);
        if (access is not null)
            value.GetString().Should().Be(access);
    }
    private void SetupOperationAccessContinuation() =>
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "The operation could not complete." }));
}
