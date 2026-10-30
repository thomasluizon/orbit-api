using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task G10_EmptyCreationLookupCannotHidePreviewOrCapacityReason(bool atCapacity, bool composition)
    {
        var parents = SetupExistingParents("pt-BR");
        if (composition) EnableComposition();
        var gate = new PayGateService(_habitRepo, _userRepo, _appConfig, _userDateService, _featureFlagService);
        _appConfig.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>())
            .Returns(atCapacity ? 2 : 10);
        _habitRepo.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => parents.Count(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
        var mediator = Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo,
            _userDateService, gate, _unitOfWork, users: _userRepo);
        var create = new CreateHabitTool(_userDateService, mediator);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, create);
        var blocks = UseProductionReadExecutor(query);
        var outcomes = new List<AgentExecuteOperationResponse>();
        UseCapacityReplayExecutor(create, outcomes);
        var reader = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(query, blocks);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == query.Name),
                Arg.Any<CancellationToken>())
            .Returns(call => reader.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        using var capture = GateTenPreviewCapture(atCapacity ? "edge-free-limit-en-r1" : "single-create-pt-r1");
        var calls = BrainDumpCalls(capture);
        SetupAiResponse(new AiResponse { ToolCalls = [calls[0]], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = [calls[1]], ConversationContext = TestConversationContext }),
                Result.Success(new AiResponse { TextMessage = "Não consegui concluir isso. Tente novamente." }));

        var result = await handler.Handle(new(UserId, "Prepare a criação solicitada.",
            ClientContext: new(SupportsParts: true, SupportsHabitListCard: true, SupportsPendingOperationChanges: true,
                PartKinds: ["habitList"], Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var operation = outcomes.Should().ContainSingle().Which.Operation;
        if (atCapacity)
        {
            operation.Status.Should().Be(AgentOperationStatus.Failed);
            operation.Payload.Should().BeOfType<Orbit.Application.Chat.Models.HabitCapacityFailure>();
            result.Value.PendingOperations.Should().BeNullOrEmpty();
            result.Value.AiMessage.Should().Be("Você chegou ao limite de 2 hábitos. Você pode adicionar um sub-hábito a um hábito existente.");
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>()
                .Which.Text.Should().Be(result.Value.AiMessage);
        }
        else
        {
            operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
            result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
                .Which.EntityName.Should().Be("Meditar");
            result.Value.AiMessage.Should().BeNullOrEmpty();
            result.Value.Parts.Should().BeEmpty();
        }
        result.Value.HabitList.Should().BeNull();
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    private static JsonDocument GateTenPreviewCapture(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GateTenPreviews", name + ".json")));
}
