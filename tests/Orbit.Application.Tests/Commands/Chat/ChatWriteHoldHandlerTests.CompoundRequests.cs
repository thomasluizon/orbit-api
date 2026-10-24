using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Application.Goals.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", false)]
    [InlineData("en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", true)]
    [InlineData("pt-BR", "Post lunch: tomar vitamina D (uma vez por semana) Before bed: tomar rosuvastatina (todo dia)", false)]
    public async Task CompoundHeadings_ModelWithoutCreationStillProducesOneBoundPreview(string language, string message, bool empty)
    {
        ParentAccount(language);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(empty ? Result.Failure<AiResponse>(ErrorMessages.AiEmptyResponse)
                : Result.Success(new AiResponse { TextMessage = "The parent already exists. Create another?", ConversationContext = new() }));
        var result = await CreateHandler().Handle(new(_userId, message), default);
        result.IsSuccess.Should().BeTrue();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(2);
        pending.Items![0].Fields.Should().Contain(field => field.Field == "parent_habit_id" && field.NewValue == "Post Lunch");
        pending.Items[1].Fields.Should().Contain(field => field.Field == "parent_habit_id" && field.NewValue == "Before Bed");
        pending.Items[0].Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Week");
        pending.Items[1].Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Day");
        result.Value.AiMessage.Should().BeNull();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task CompoundActions_RenameAndNewChildShareOneApproval()
    {
        var parents = ParentAccount("en");
        _unitOfWork.PassThroughTransactions<Result<string>>();
        var update = new UpdateHabitCommandHandler(_habitRepo, Substitute.For<IGenericRepository<SentReminder>>(),
            _goalRepo, _payGate, Substitute.For<IGoalCompletionService>(), _userDateService, _unitOfWork, _flagCache, _tagRepo,
            Substitute.For<IAppConfigService>());
        _mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => update.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        ScriptToolCall("{}", "update_habit");
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [
                    new("update_habit", "rename", JsonSerializer.SerializeToElement(new { habit_id = parents[0].Id, title = "After Lunch" })),
                    new("create_sub_habit", "child", JsonSerializer.SerializeToElement(new { parent_habit_id = parents[1].Id, title = "Take magnesium", frequency_unit = "Day" }))
                ],
                ConversationContext = new()
            }));
        var result = await CreateHandler().Handle(new(_userId, "Rename Post Lunch to After Lunch and add Take magnesium under Before Bed"), default);
        result.IsSuccess.Should().BeTrue();
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items.Should().HaveCount(2);
        result.Value.AiMessage.Should().BeNull();
        parents[0].Title.Should().Be("Post Lunch");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        var stored = _pendingOperationStore.GetExecution(_userId, preview.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, preview.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        parents[0].Title.Should().Be("After Lunch");
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.Title == "Take magnesium"
            && habit.ParentHabitId == parents[1].Id), Arg.Any<CancellationToken>());
        var replay = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        replay.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
    }

    [Theory]
    [InlineData("Add Vitamin C to whichever routine fits best")]
    [InlineData("Coloque vitamina C na rotina que combina melhor")]
    public async Task CompoundParentSuggestion_OffersExistingParentAndRootChoice(string message)
    {
        var parents = ParentAccount("en");
        ScriptToolCall(JsonSerializer.Serialize(new { habits = new[] { new { title = "Vitamin C", parent_habit_id = parents[0].Id, frequency_unit = "Day" } } }), "bulk_create_habits");
        var result = await CreateHandler().Handle(new(_userId, message), default);
        result.IsSuccess.Should().BeTrue();
        var item = result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle().Which;
        item.FieldOptions.Should().ContainSingle().Which.Required.Should().BeFalse();
        item.FieldOptions![0].Options.Should().Contain(option => option.Value.ValueKind == JsonValueKind.Null);
        item.Fields.Should().Contain(field => field.Field == "parent_habit_id" && field.NewValue == "Post Lunch");
    }

    [Fact]
    public async Task CompoundDuplicate_CreateRecognisesChildAndReusesIt()
    {
        var parents = ParentAccount("en");
        var water = Habit.Create(new HabitCreateParams(_userId, "Drink water", Orbit.Domain.Enums.FrequencyUnit.Day, 1,
            Today, ParentHabitId: parents[0].Id)).Value;
        UseParentHabits([.. parents, water]);
        ScriptToolCall("""{"title":"Drink water","frequency_unit":"Day","due_time":"07:00"}""");
        var result = await CreateHandler().Handle(new(_userId, "Create a habit to drink water every morning"), default);
        var item = result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle().Which;
        item.EntityId.Should().Be(water.Id);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

}
