using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Fact]
    public async Task DeleteAndRename_ShareOneApproval()
    {
        var parents = ParentAccount("en");
        UseRealUpdates();
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        ScriptToolCalls(
            new("update_habit", "rename", JsonSerializer.SerializeToElement(new { habit_id = parents[0].Id, title = "After Lunch" })),
            new("delete_habit", "delete", JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id })));

        var result = await CreateHandler().Handle(new(_userId, "Rename Post Lunch to After Lunch and delete Before Bed"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items.Should().HaveCount(2);
        var approved = await ApproveAsync(preview.Id);
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        parents[0].Title.Should().Be("After Lunch");
        await _mediator.Received(1).Send(Arg.Is<DeleteHabitCommand>(command => command.HabitId == parents[1].Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogGoalLinkTagAndDelete_ShareOneApproval()
    {
        var parents = ParentAccount("en");
        var goal = Goal.Create(new Goal.CreateGoalParams(_userId, "Sleep better", 10, "nights")).Value;
        var tag = Tag.Create(_userId, "Health", "#7c3aed").Value;
        _goalRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([goal]);
        _goalRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(), Arg.Any<CancellationToken>()).Returns([goal]);
        _tagRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([tag]);
        _tagRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Tag, bool>>>(),
            Arg.Any<Func<IQueryable<Tag>, IQueryable<Tag>>?>(), Arg.Any<CancellationToken>()).Returns([tag]);
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LogHabitResponse(Guid.NewGuid(), true, 1)));
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        ScriptToolCalls(
            new("log_habit", "log", JsonSerializer.SerializeToElement(new { habit_id = parents[0].Id })),
            new("link_goals_to_habit", "link", JsonSerializer.SerializeToElement(new { habit_id = parents[0].Id, goal_ids = new[] { goal.Id } })),
            new("assign_tags", "tag", JsonSerializer.SerializeToElement(new { habit_id = parents[0].Id, tag_ids = new[] { tag.Id } })),
            new("delete_habit", "delete", JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id })));

        var result = await CreateHandler().Handle(new(_userId,
            "Log Post Lunch, link it to Sleep better, tag it Health and delete Before Bed"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        _pendingOperationStore.GetExecution(_userId, preview.Id)!.OperationId.Should().Be(AccountChangeSet.OperationName);
        AccountChangeSet.Read(_pendingOperationStore.GetExecution(_userId, preview.Id)!.Arguments)!
            .Select(member => member.OperationId).Should()
            .BeEquivalentTo(["log_habit", "link_goals_to_habit", "assign_tags", "delete_habit"]);
    }

    private void UseRealUpdates()
    {
        _unitOfWork.PassThroughTransactions<Result<string>>();
        var update = new UpdateHabitCommandHandler(_habitRepo, Substitute.For<IGenericRepository<SentReminder>>(),
            _goalRepo, _payGate, Substitute.For<IGoalCompletionService>(), _userDateService, _unitOfWork, _flagCache, _tagRepo,
            Substitute.For<IAppConfigService>());
        _mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => update.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
    }

    private void ScriptToolCalls(params AiToolCall[] calls)
    {
        ScriptToolCall("{}", calls[0].Name);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = [.. calls], ConversationContext = new() }));
    }

    private Task<AgentExecuteOperationResponse> ApproveAsync(Guid pendingId)
    {
        var stored = _pendingOperationStore.GetExecution(_userId, pendingId)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pendingId)!;
        return _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
    }
}
