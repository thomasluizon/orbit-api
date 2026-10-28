using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Chat;

public sealed partial class AstraApprovalPathTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task Suggestion_TimeOnlyAdjustmentPreservesExistingFieldsThroughApproval(string language, bool previewAll)
    {
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId,
            language == "en" ? "Read" : "Ler", FrequencyUnit.Day, 1, _today,
            Description: "Keep this description", Days: [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
            DueTime: new(22, 0), DueEndTime: new(22, 15), ReminderEnabled: true, ReminderTimes: [10, 30],
            ChecklistItems: [new("Choose a book", true), new("Read a chapter", false)],
            EndDate: _today.AddDays(60), Emoji: "📖", IntervalWeeks: 2, StartDate: _today.AddDays(-14))).Value;
        existing.PostponeTo(_today);
        _db.Habits.Add(existing);
        await _db.SaveChangesAsync();
        var habits = new GenericRepository<Habit>(_db);
        var goals = new GenericRepository<Goal>(_db);
        var generator = Substitute.For<IHabitDraftGenerator>();
        var modelDraft = new HabitSuggestionDraft(Guid.NewGuid(), existing.Emoji!, existing.Title, "Habit",
            existing.FrequencyUnit, existing.FrequencyQuantity, existing.Days.ToArray(), existing.NextDueDate,
            new(22, 30), new(22, 45), 10, existing.ChecklistItems.Select(item => item.Text).ToArray(),
            "Move the reading time.", existing.Id);
        generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitSuggestionDraft>>([modelDraft]));
        var suggestions = new HabitSuggestionService(habits, goals, _dates, generator);
        var instruction = language == "en" ? "Move only the time to 22:30" : "Mude só o horário para 22h30";
        var generated = await suggestions.GenerateAsync(_userId, instruction, language, 1, null, default);
        generated.IsSuccess.Should().BeTrue(generated.Error);
        var draft = generated.Value.Drafts.Should().ContainSingle().Which;
        draft.Days.Should().Equal(existing.Days);
        draft.ChecklistItems.Should().Equal(existing.ChecklistItems.Select(item => item.Text));

        var unit = Substitute.For<IUnitOfWork>();
        unit.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _db.SaveChangesAsync(call.Arg<CancellationToken>()));
        unit.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<string>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<string>>>>()(call.Arg<CancellationToken>()));
        var handler = new UpdateHabitCommandHandler(habits, new GenericRepository<SentReminder>(_db), goals,
            Substitute.For<IPayGateService>(), Substitute.For<Orbit.Application.Goals.Services.IGoalCompletionService>(),
            _dates, unit, _cache, new GenericRepository<Tag>(_db), Substitute.For<IAppConfigService>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        var tool = new UpdateHabitTool(habits, _dates, mediator);
        var (executor, store, _) = Build(tool);
        var changes = new AccountChangeSetService(store, Substitute.For<IPendingOperationChangePreviewer>(),
            new AgentCatalogService([tool]), new AiToolRegistry([tool]));
        var preview = previewAll
            ? await new PreviewAllHabitSuggestionsCommandHandler(suggestions, executor, changes, store)
                .Handle(new(_userId, [draft], language), default)
            : await new PreviewHabitSuggestionCommandHandler(suggestions, executor)
                .Handle(new(_userId, draft, language), default);
        preview.IsSuccess.Should().BeTrue(preview.Error);
        preview.Value.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pendingId = preview.Value.PendingOperation!.Id;
        var pending = store.GetExecution(_userId, pendingId)!;
        var confirmation = store.Confirm(_userId, pendingId)!;

        var approved = await executor.ExecuteAsync(new(_userId, tool.Name, pending.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _db.ChangeTracker.Clear();
        var saved = await _db.Habits.SingleAsync(habit => habit.Id == existing.Id);
        saved.Days.Should().Equal(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday);
        saved.ChecklistItems.Should().Equal(new ChecklistItem("Choose a book", true), new ChecklistItem("Read a chapter", false));
        saved.ReminderEnabled.Should().BeTrue();
        saved.ReminderTimes.Should().Equal(10, 30);
        saved.IntervalWeeks.Should().Be(2);
        saved.StartDate.Should().Be(_today.AddDays(-14));
        saved.EndDate.Should().Be(_today.AddDays(60));
        saved.Description.Should().Be(existing.Description);
        saved.DueTime.Should().Be(new TimeOnly(22, 30));
        saved.DueEndTime.Should().Be(new TimeOnly(22, 45));
        var previewFields = preview.Value.PendingOperation.Items!.Single().Fields;
        previewFields.Should().NotContain(field => field.Field == "checklist_items" || field.Field == "reminder_times"
            || field.Field == "reminder_enabled" || field.Field == "interval_weeks"
            || field.Field == "start_date" || field.Field == "end_date");
        previewFields.Should().Contain(field => field.Field == "due_time" && field.NewValue == "22:30");
        previewFields.Should().Contain(field => field.Field == "due_end_time" && field.NewValue == "22:45");
    }
}
