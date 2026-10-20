using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_UnlogHabit_RecalculatesStreakOnlyAfterSuccess(bool success)
    {
        SetupUserAndPayGate();
        var tool = Substitute.For<IAiTool>();
        tool.Name.Returns("unlog_habit");
        tool.Description.Returns("Undo a habit completion");
        tool.IsReadOnly.Returns(false);
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(success, EntityName: "Water", Error: success ? null : "No log found"));
        var handler = CreateHandler(tool);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall("unlog_habit", "undo", JsonSerializer.SerializeToElement(new { }))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(new AiResponse { TextMessage = "Pronto." }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Desmarque o de hoje"), default);

        result.IsSuccess.Should().BeTrue();
        await _userStreakService.Received(success ? 1 : 0)
            .RecalculateAsync(UserId, cancellationToken: Arg.Any<CancellationToken>());
    }
}
