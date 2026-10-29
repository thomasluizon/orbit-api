using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task BrainDump_GateNineNewIdeaPreviewPersistsOnlyAfterApproval(string language)
    {
        var today = new DateOnly(2026, 10, 29);
        _provider.GetRequiredService<IUserDateService>().GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId,
            language == "en" ? "Water" : "Beber água", FrequencyUnit.Day, 1, today)).Value;
        _factory.Context.Habits.Add(existing);
        await _factory.Context.SaveChangesAsync();
        using var baseline = _factory.CreateContext();
        var before = JsonSerializer.Serialize(baseline.Habits.Single(habit => habit.Id == existing.Id));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "BrainDumps", $"g9d-brain-dump-ideas-{language}-r1.json")));
        var arguments = TerminalWriteDirective.ExecutionArguments(fixture.RootElement.GetProperty("toolCalls").EnumerateArray()
            .Single(call => call.GetProperty("name").GetString() == "bulk_create_habits").GetProperty("arguments"));
        var request = await ConfirmAsync(arguments);
        using (var held = _factory.CreateContext())
            held.Habits.Should().ContainSingle().Which.Id.Should().Be(existing.Id);

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, _lastAudit?.Error);
        using var saved = _factory.CreateContext();
        var rows = saved.Habits.ToArray();
        rows.Should().HaveCount(4);
        JsonSerializer.Serialize(rows.Single(habit => habit.Id == existing.Id)).Should().Be(before);
        foreach (var item in arguments.GetProperty("habits").EnumerateArray())
        {
            var persisted = rows.Single(habit => habit.Title == item.GetProperty("title").GetString());
            persisted.NextDueDate.Should().Be(DateOnly.Parse(item.GetProperty("due_date").GetString()!));
            var time = item.GetProperty("due_time");
            persisted.DueTime.Should().Be(time.ValueKind == JsonValueKind.Null ? null : TimeOnly.Parse(time.GetString()!));
        }
        var ideaTitle = fixture.RootElement.GetProperty("suggestionResult").GetProperty("drafts")[0].GetProperty("title").GetString();
        rows.Should().ContainSingle(habit => habit.Title == ideaTitle && habit.Schedule == HabitSchedule.Recurring);
        _pending.GetAwaiting(_userId).Should().BeEmpty();
    }
}
