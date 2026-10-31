using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneSmallStep_CapturedChecklistAdditionCannotChangeStoredItems(bool composition)
    {
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "ChecklistSaves", "composition-one-small-step-save-checklist-pt-BR-r3.json")));
        var fixture = capture.RootElement.GetProperty("groundingEvidence").GetProperty("fixture")
            .GetProperty("habits")[1];
        var existing = fixture.GetProperty("checklistItems").EnumerateArray()
            .Select(item => new ChecklistItem(item.GetProperty("text").GetString()!, item.GetProperty("isChecked").GetBoolean())).ToArray();
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId,
            fixture.GetProperty("title").GetString()!, FrequencyUnit.Day, 1, Today, ChecklistItems: existing)).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();
        var repository = new GenericRepository<Habit>(_dbContext);
        _habitRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.FindAsync(call.Arg<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), call.Arg<CancellationToken>()));
        _habitRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.FindAsync(call.Arg<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
                call.ArgAt<Func<IQueryable<Habit>, IQueryable<Habit>>?>(1), call.Arg<CancellationToken>()));
        _habitRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.GetByIdAsync(call.Arg<Guid>(), call.Arg<CancellationToken>()));
        _habitRepo.FindOneTrackedAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.FindOneTrackedAsync(call.Arg<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
                call.ArgAt<Func<IQueryable<Habit>, IQueryable<Habit>>?>(1), call.Arg<CancellationToken>()));
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Tools.CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        var append = new AppendChecklistItemCommandHandler(repository, _unitOfWork, _flagCache, _userDateService);
        _mediator.Send(Arg.Any<AppendChecklistItemCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            append.Handle(call.Arg<AppendChecklistItemCommand>(), call.Arg<CancellationToken>()));
        if (composition)
            _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        var capturedCall = capture.RootElement.GetProperty("toolCalls")[1];
        var arguments = capturedCall.GetProperty("arguments").GetRawText().Replace("[redacted]", habit.Id.ToString(), StringComparison.Ordinal);
        ScriptToolCall(arguments, capturedCall.GetProperty("name").GetString()!);

        var result = await CreateHandler().Handle(new(_userId, "Quero salvar o passo no checklist.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Failed);
        result.Value.PolicyDenials.Should().BeNullOrEmpty();
        result.Value.Operations.Single().PolicyReason.Should().Contain("preserve every stored item's text, order and checked state");
        habit.ChecklistItems.Should().Equal(existing);
        _dbContext.PendingAgentOperations.Should().BeEmpty();

        var step = capturedCall.GetProperty("arguments").GetProperty("checklist_items")[existing.Length].GetProperty("text").GetString()!;
        ScriptToolCall(JsonSerializer.Serialize(new { habit_id = habit.Id, item_to_add = step, orbit_write_complete = true }), "update_checklist");
        var held = await CreateHandler().Handle(new(_userId, "Quero adicionar o passo ao checklist.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true)), default);
        held.IsSuccess.Should().BeTrue(held.Error);
        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;
        var previewItems = pending.Items.Should().ContainSingle().Which.Fields
            .Should().ContainSingle(field => field.Field == "checklist_items").Which.ProposedValue!.Value;
        var expected = existing.Append(new ChecklistItem(step, false)).ToArray();
        previewItems.EnumerateArray().Select(item => new ChecklistItem(item.GetProperty("text").GetString()!,
            item.GetProperty("is_checked").GetBoolean())).Should().Equal(expected);
        (await _dbContext.Habits.AsNoTracking().SingleAsync()).ChecklistItems.Should().Equal(existing);
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;

        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _dbContext.ChangeTracker.Clear();
        var readback = await new GetHabitByIdQueryHandler(repository, new GenericRepository<HabitLog>(_dbContext),
            _userDateService, repeatReminderReader: Substitute.For<Orbit.Domain.Interfaces.IRepeatReminderReader>())
            .Handle(new(_userId, habit.Id), default);
        readback.IsSuccess.Should().BeTrue(readback.Error);
        readback.Value.ChecklistItems.Select(item => new ChecklistItem(item.Text, item.IsChecked)).Should().Equal(expected);
    }
}
