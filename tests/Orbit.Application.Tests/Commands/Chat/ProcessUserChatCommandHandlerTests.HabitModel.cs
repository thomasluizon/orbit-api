using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Chat;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MultipleCreateCalls_PreserveSingleCreateSchedules(bool datedChild, bool addSibling)
    {
        SetupUserAndPayGate();
        _unitOfWork.PassThroughTransactions<ToolResult>();
        _unitOfWork.PassThroughTransactions<Result<BulkCreateResult>>();
        _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _appConfig.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>())
            .Returns(AppConstants.MaxHabitDepth);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = string.Empty }));
        var saved = new List<Habit>();
        await _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var bulkHandler = new BulkCreateHabitsCommandHandler(
            new(_habitRepo, Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>(), _tagRepo),
            _payGate, _userDateService, _unitOfWork, cache, _appConfig);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => bulkHandler.Handle(call.Arg<BulkCreateHabitsCommand>(), call.Arg<CancellationToken>()));
        var single = RecoveryCreateHabitTool();
        var handler = CreateHandler(single, new BulkCreateHabitsTool(mediator));
        var arguments = datedChild
            ? """{"title":"Classes","frequency_unit":"Day","frequency_quantity":1,"days":["Wednesday"],"sub_habits":[{"title":"Extra class","due_date":"2026-08-27"}]}"""
            : """{"title":"Classes","frequency_unit":"Week","frequency_quantity":1,"days":["Thursday"]}""";
        var calls = new List<AiToolCall> { new(single.Name, "classes", ParseArguments(arguments)) };
        if (addSibling)
            calls.Add(new(single.Name, "book", ParseArguments("""{"title":"Buy book","frequency_unit":"OneTime"}""")));
        SetupAiResponse(new AiResponse { ToolCalls = calls, ConversationContext = TestConversationContext });

        var response = await handler.Handle(new ProcessUserChatCommand(UserId,
            "Create recurring classes every week, including the extra class, and buy a book."), default);

        response.IsSuccess.Should().BeTrue();
        saved.Should().HaveCount((datedChild ? 2 : 1) + (addSibling ? 1 : 0),
            "chat returned {0}", JsonSerializer.Serialize(response.Value));
        var root = saved.Single(habit => habit.Title == "Classes");
        root.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        root.FrequencyQuantity.Should().Be(1);
        root.Days.Should().Equal(datedChild ? DayOfWeek.Wednesday : DayOfWeek.Thursday);
        if (datedChild)
        {
            var child = saved.Single(habit => habit.Title == "Extra class");
            child.ParentHabitId.Should().Be(root.Id);
            child.FrequencyUnit.Should().Be(FrequencyUnit.Day);
            child.FrequencyQuantity.Should().Be(1);
            child.NextDueDate.Should().Be(new DateOnly(2026, 8, 27));
            child.Days.Should().BeEmpty();
        }
        if (addSibling)
            saved.Single(habit => habit.Title == "Buy book").FrequencyUnit.Should().BeNull();
    }

    [Theory]
    [InlineData("Start running tomorrow, every other day", "Day", 2, false)]
    [InlineData("Começar a correr amanhã, dia sim dia não", "Day", 2, false)]
    [InlineData("Start running tomorrow, three times a week", "Week", 3, true)]
    [InlineData("Começar a correr amanhã, três vezes por semana", "Week", 3, true)]
    public async Task ExplicitCadenceWithStartDate_DispatchesRecurringCreate(
        string message, string unit, int quantity, bool flexible)
    {
        SetupUserAndPayGate();
        _unitOfWork.PassThroughTransactions<ToolResult>();
        _payGate.CanCreateHabits(UserId, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = string.Empty }));
        var saved = new List<Habit>();
        await _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>());
        var single = RecoveryCreateHabitTool();
        var handler = CreateHandler(single);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(single.Name, "run", JsonSerializer.SerializeToElement(new
            {
                title = "Run", frequency_unit = unit, frequency_quantity = quantity,
                is_flexible = flexible, due_date = "2026-10-23"
            }))],
            ConversationContext = TestConversationContext
        });

        var response = await handler.Handle(new ProcessUserChatCommand(UserId, message), default);

        response.IsSuccess.Should().BeTrue();
        saved.Should().ContainSingle().Which.Should().Match<Habit>(habit =>
            habit.FrequencyUnit.ToString() == unit && habit.FrequencyQuantity == quantity
            && (habit.Schedule == HabitSchedule.Flexible) == flexible && habit.NextDueDate == new DateOnly(2026, 10, 23));
    }

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
