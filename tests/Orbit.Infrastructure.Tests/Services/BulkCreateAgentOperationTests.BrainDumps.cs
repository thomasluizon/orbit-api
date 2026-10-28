using NSubstitute;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests
{
    [Theory]
    [InlineData(HabitSchedule.Recurring, false, "Habit")]
    [InlineData(HabitSchedule.Flexible, false, "Habit")]
    [InlineData(HabitSchedule.General, false, "General")]
    [InlineData(HabitSchedule.Recurring, true, "Task")]
    public async Task BrainDump_ChildGroupingMatchesTheScheduleSavedByTheCommand(HabitSchedule schedule, bool oneTime, string type)
    {
        var today = new DateOnly(2026, 3, 20);
        var parent = Habit.Create(new HabitCreateParams(schedule, _userId, "Parent",
            schedule == HabitSchedule.General ? null : FrequencyUnit.Day,
            schedule == HabitSchedule.General ? null : 1, today)).Value;
        _factory.Context.Habits.Add(parent);
        await _factory.Context.SaveChangesAsync();
        var child = new Dictionary<string, object> { ["title"] = "Child" };
        if (oneTime) child["frequency_unit"] = "OneTime";
        var arguments = JsonSerializer.SerializeToElement(new { parent_habit_id = parent.Id, habits = new[] { child } });
        var request = await ConfirmAsync(arguments);
        var preview = await _previewer.PreviewAsync(_userId, request.OperationId, arguments);
        preview.Value!.Items.Should().ContainSingle().Which.HabitType.Should().Be(type);

        var result = await _executor.ExecuteAsync(request);

        result.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, _lastAudit?.Error);
        using var saved = _factory.CreateContext();
        var added = saved.Habits.Single(habit => habit.ParentHabitId == parent.Id);
        var reader = new GetHabitByIdQueryHandler(new GenericRepository<Habit>(saved),
            new GenericRepository<HabitLog>(saved), _provider.GetRequiredService<IUserDateService>(), repeatReminderReader: Substitute.For<IRepeatReminderReader>());
        var readback = await reader.Handle(new(_userId, added.Id), default);
        readback.IsSuccess.Should().BeTrue(readback.Error);
        readback.Value.Schedule.Should().Be(type == "General" ? HabitSchedule.General
            : oneTime ? HabitSchedule.OneTime : HabitSchedule.Recurring);
    }

    [Theory]
    [InlineData("en", "approve")]
    [InlineData("pt-BR", "approve")]
    [InlineData("en", "edit")]
    [InlineData("pt-BR", "edit")]
    [InlineData("en", "reject")]
    [InlineData("pt-BR", "reject")]
    public async Task BrainDump_ApprovalEditAndRejectUseExistingCommandsWithPersistedReadback(string language, string action)
    {
        var today = new DateOnly(2026, 3, 20);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrainDumps", language + ".json"));
        using var original = JsonDocument.Parse(source);
        var title = original.RootElement.GetProperty("existingTitle").GetString()!;
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, title,
            FrequencyUnit.Day, 1, today, Description: "Keep this description", DueTime: new(20, 0),
            DueEndTime: new(22, 0), Emoji: "📖", ReminderEnabled: true, ReminderTimes: [10],
            EndDate: today.AddDays(30))).Value;
        _factory.Context.Habits.Add(existing);
        await _factory.Context.SaveChangesAsync();
        using var fixture = JsonDocument.Parse(source.Replace("__existing__", existing.Id.ToString())
            .Replace("__today__", today.ToString("yyyy-MM-dd"))
            .Replace("__tomorrow__", today.AddDays(1).ToString("yyyy-MM-dd")));
        var call = fixture.RootElement.GetProperty("toolCalls")[0].Deserialize<AiToolCall>(JsonSerializerOptions.Web)!;
        var request = new AgentExecuteOperationRequest(_userId, call.Name, call.Args,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true);

        var staged = await _executor.ExecuteAsync(request);

        staged.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pending = staged.PendingOperation!;
        pending.Items.Should().HaveCount(3);
        pending.Items!.Select(item => item.HabitType).Should().Equal("Task", "Habit", "Habit");
        pending.Items[2].EntityId.Should().Be(existing.Id);
        _pending.GetAwaiting(_userId).Should().ContainSingle();
        using (var before = _factory.CreateContext())
        {
            before.Habits.Should().ContainSingle().Which.DueTime.Should().Be(new TimeOnly(20, 0));
        }

        if (action == "reject")
        {
            var held = _pending.GetExecution(_userId, pending.Id)!;
            _pending.Cancel(_userId, pending.Id,
                AgentOperationFingerprint.Compute(held.OperationId, held.Arguments.GetRawText())).Should().BeTrue();
            _pending.Confirm(_userId, pending.Id).Should().BeNull();
            using var rejected = _factory.CreateContext();
            rejected.Habits.Should().ContainSingle().Which.DueTime.Should().Be(new TimeOnly(20, 0));
            return;
        }

        if (action == "edit")
        {
            var revisions = new PendingOperationRevisionService(_pending, _previewer,
                new RevisePendingOperationRequestValidator(), _registry);
            var revised = await revisions.ReviseAsync(_userId, pending.Id, new(pending.PreviewFingerprint!,
                pending.Items.Select((item, index) => new RevisedPendingOperationItem(item.ItemId,
                    index == 0 ? JsonSerializer.SerializeToElement(new { frequency_unit = "Day", frequency_quantity = 1 }) : null)).ToArray()), default);
            revised.IsSuccess.Should().BeTrue(revised.Error);
            revised.Preview!.Items!.Select(item => item.HabitType).Should().Equal("Habit", "Habit", "Habit");
            using var beforeApproval = _factory.CreateContext();
            beforeApproval.Habits.Should().ContainSingle().Which.DueTime.Should().Be(new TimeOnly(20, 0));
        }

        var confirmation = _pending.Confirm(_userId, pending.Id)!;
        var execution = _pending.GetExecution(_userId, pending.Id)!;
        var result = await _executor.ExecuteAsync(request with
        { Arguments = execution.Arguments, ConfirmationToken = confirmation.ConfirmationToken });

        result.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, _lastAudit?.Error);
        using var saved = _factory.CreateContext();
        var rows = saved.Habits.ToList();
        rows.Should().HaveCount(3);
        rows.Count(habit => habit.Title == title).Should().Be(1);
        var dates = _provider.GetRequiredService<IUserDateService>();
        var reader = new GetHabitByIdQueryHandler(new GenericRepository<Habit>(saved), new GenericRepository<HabitLog>(saved), dates, repeatReminderReader: Substitute.For<IRepeatReminderReader>());
        foreach (var row in rows)
        {
            var readback = await reader.Handle(new(_userId, row.Id), default);
            readback.IsSuccess.Should().BeTrue(readback.Error);
            readback.Value.Title.Should().Be(row.Title);
            if (row.Id == existing.Id)
            {
                readback.Value.DueTime.Should().Be(new TimeOnly(21, 0));
                readback.Value.FrequencyUnit.Should().Be(FrequencyUnit.Day);
                readback.Value.Description.Should().Be("Keep this description");
                readback.Value.DueEndTime.Should().Be(new TimeOnly(22, 0));
                readback.Value.EndDate.Should().Be(today.AddDays(30));
                readback.Value.ReminderTimes.Should().Equal(10);
                readback.Value.Emoji.Should().Be("📖");
            }
            else
            {
                var index = pending.Items.ToList().FindIndex(item => item.EntityName == row.Title);
                index.Should().BeOneOf(0, 1);
                readback.Value.Schedule.Should().Be(index == 0 && action == "approve" ? HabitSchedule.OneTime : HabitSchedule.Recurring);
                readback.Value.DueDate.Should().Be(index == 0 ? today.AddDays(1) : today);
            }
        }
        _pending.GetAwaiting(_userId).Should().BeEmpty();
    }
}
