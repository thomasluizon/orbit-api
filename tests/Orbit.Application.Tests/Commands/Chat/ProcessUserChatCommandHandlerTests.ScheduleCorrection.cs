using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task ScheduleCertification_DatedOppositeWeekdayCannotMatchUnsupportedOriginal()
    {
        SetupUserAndPayGate();
        var today = new DateOnly(2026, 10, 1);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var tool = RecoveryCreateHabitTool();
        const string original = """{"title":"Read","days":["Monday"]}""";
        const string candidate = """{"title":"Read","days":["Tuesday"],"due_date":"2026-10-01"}""";
        (await tool.CheckArgumentsAsync(ParseArguments(original), UserId, default)).IsFailure.Should().BeTrue();
        (await tool.CheckArgumentsAsync(ParseArguments(candidate), UserId, default)).IsSuccess.Should().BeTrue();
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool, hold: true);
        SetupAiResponse(ToolResponse(tool.Name, "unsupported_schedule", original));
        QueueCorrection(ParseArguments(candidate));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Read every Monday."), default);

        gateway.Requests.Should().ContainSingle();
        gateway.Responses[0].Should().BeOfType<AgentExecuteOperationResponse>();
        result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Success);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(item =>
                JsonSerializer.Serialize(item.Payload, (JsonSerializerOptions?)null).Contains("correction_allowed"))),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
