using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(HabitProposalValidatorTests.PortugueseExamMessage)]
    [InlineData(HabitProposalValidatorTests.EnglishExamMessage)]
    public async Task ExamFirstTurn_RejectsOpenEndedPreparationBeforeDispatch(string message)
    {
        SetupUserAndPayGate();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = string.Empty }));
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        SetupAiResponse(ToolResponse(tool.Name, "preparation",
            """{"title":"Não tomar multivitamínico","frequency_unit":"Day","due_date":"2026-10-27"}"""));
        var response = await handler.Handle(new ProcessUserChatCommand(UserId, message), default);
        response.IsSuccess.Should().BeTrue();
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HabitProposalValidatorTests.PortugueseExamMessage)]
    [InlineData(HabitProposalValidatorTests.EnglishExamMessage)]
    public async Task ExamFirstTurn_MultipleCreateCallsDispatchOneBatch(string message)
    {
        SetupUserAndPayGate();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = string.Empty }));
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkCreateResult([])));
        var single = RecoveryCreateHabitTool();
        var bulk = new BulkCreateHabitsTool(mediator);
        var handler = CreateHandler(single, bulk);
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new AiToolCall(single.Name, "first", JsonSerializer.SerializeToElement(new
                {
                    title = "Exame de colesterol", frequency_unit = "OneTime", due_date = "2026-10-26", due_time = "15:50"
                })),
                new AiToolCall(single.Name, "second", JsonSerializer.SerializeToElement(new
                {
                    title = "Outros exames de sangue", frequency_unit = "OneTime", due_date = "2026-10-30", due_time = "13:30"
                }))
            ],
            ConversationContext = TestConversationContext
        });
        var response = await handler.Handle(new ProcessUserChatCommand(UserId, message), default);
        response.IsSuccess.Should().BeTrue();
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == bulk.Name && request.Arguments.GetProperty("habits").GetArrayLength() == 2), Arg.Any<CancellationToken>());
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == single.Name), Arg.Any<CancellationToken>());
    }
}
