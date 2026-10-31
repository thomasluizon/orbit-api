using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Chat;

public sealed partial class AstraApprovalPathTests
{
    [Theory]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", false, true)]
    [InlineData("pt-BR", false, true)]
    public async Task Suggestion_CadenceClearsStayVisibleThroughBulkApproval(string language, bool oneTime, bool reviseEnd)
    {
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId,
            language == "en" ? "Read" : "Ler", FrequencyUnit.Day, 1, _today,
            EndDate: _today.AddDays(30), Emoji: "📖")).Value;
        _db.Habits.Add(existing);
        await _db.SaveChangesAsync();
        var draft = new HabitSuggestionDraft(Guid.NewGuid(), existing.Emoji!, existing.Title,
            oneTime ? "Task" : "Habit", oneTime ? null : FrequencyUnit.Day, oneTime ? null : 2, [],
            _today, new(8, 5), new(8, 10), null, [], "Adjust the cadence.", existing.Id);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habits = new[] { PreviewHabitSuggestionCommandHandler.Arguments(draft, existing) }
        });
        var habits = new GenericRepository<Habit>(_db);
        var unit = Substitute.For<IUnitOfWork>();
        unit.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _db.SaveChangesAsync(call.Arg<CancellationToken>()));
        unit.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<string>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<string>>>>()(call.Arg<CancellationToken>()));
        var update = new UpdateHabitCommandHandler(habits, new GenericRepository<SentReminder>(_db),
            new GenericRepository<Goal>(_db), Substitute.For<IPayGateService>(),
            Substitute.For<Orbit.Application.Goals.Services.IGoalCompletionService>(), _dates, unit, _cache,
            new GenericRepository<Tag>(_db), Substitute.For<IAppConfigService>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => update.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        var tool = new BulkCreateHabitsTool(mediator);
        var (executor, store, revisions) = Build(tool);

        var held = await executor.ExecuteAsync(new(_userId, tool.Name, arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));

        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var preview = held.PendingOperation!;
        var endDate = preview.Items!.Single().Fields.Should().ContainSingle(field => field.Field == "end_date").Which;
        endDate.OldValue.Should().Be(_today.AddDays(30).ToString("yyyy-MM-dd"));
        endDate.NewValue.Should().BeNull();
        endDate.ProposedValue!.Value.ValueKind.Should().Be(JsonValueKind.Null);
        preview.PreviewFingerprint.Should().NotBeNullOrEmpty();
        var pending = store.GetExecution(_userId, preview.Id)!;
        (await revisions.IsCurrentAsync(_userId, pending, default)).Should().BeTrue();
        if (reviseEnd)
        {
            var revision = await revisions.ReviseAsync(_userId, preview.Id,
                new(preview.PreviewFingerprint!, [new(preview.Items!.Single().ItemId,
                    JsonSerializer.SerializeToElement(new { end_date = _today.AddDays(60).ToString("yyyy-MM-dd") }))]), default);
            revision.IsSuccess.Should().BeTrue(revision.Error);
            revision.Preview!.Items!.Single().Fields.Should().Contain(field =>
                field.Field == "end_date" && field.NewValue == _today.AddDays(60).ToString("yyyy-MM-dd"));
            revision.Preview.PreviewFingerprint.Should().NotBe(preview.PreviewFingerprint);
            pending = store.GetExecution(_userId, preview.Id)!;
        }
        existing.EndDate.Should().Be(_today.AddDays(30));
        existing.FrequencyQuantity.Should().Be(1);
        await mediator.DidNotReceive().Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>());
        var confirmation = store.Confirm(_userId, preview.Id)!;

        var approved = await executor.ExecuteAsync(new(_userId, tool.Name, pending.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(approved));
        _db.ChangeTracker.Clear();
        var saved = await _db.Habits.SingleAsync(habit => habit.Id == existing.Id);
        saved.EndDate.Should().Be(reviseEnd ? _today.AddDays(60) : null);
        saved.FrequencyUnit.Should().Be(oneTime ? null : FrequencyUnit.Day);
        saved.FrequencyQuantity.Should().Be(oneTime ? 1 : 2);
        saved.Schedule.Should().Be(oneTime ? HabitSchedule.OneTime : HabitSchedule.Recurring);
        saved.DueTime.Should().Be(draft.DueTime);
        saved.DueEndTime.Should().Be(draft.DueEndTime);
        await mediator.Received(1).Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>());
    }
}
