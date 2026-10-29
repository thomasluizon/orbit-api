using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using NSubstitute;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests
{
    [Theory]
    [InlineData("reuse-en-r1", "Water")]
    [InlineData("reuse-en-r2", "Water")]
    [InlineData("reuse-en-r3", "Water")]
    [InlineData("reuse-pt-BR-r2", "Beber água")]
    [InlineData("mixed-en-r3", "Water")]
    [InlineData("mixed-pt-BR-r2", "Beber água")]
    public async Task BrainDump_GateNineReuseAndStartDateSurviveApproval(string name, string title)
    {
        var today = new DateOnly(2026, 10, 29);
        _provider.GetRequiredService<IUserDateService>().GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId,
            title, FrequencyUnit.Day, 1, today, Description: "Keep this description")).Value;
        _factory.Context.Habits.Add(existing);
        await _factory.Context.SaveChangesAsync();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "BrainDumps", "g9a-brain-dump-" + name + ".json")).Replace("[redacted]", existing.Id.ToString()));
        var arguments = fixture.RootElement.GetProperty("toolCalls").EnumerateArray()
            .Single(call => call.GetProperty("name").GetString() == "bulk_create_habits").GetProperty("arguments");
        var request = await ConfirmAsync(arguments);

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, _lastAudit?.Error);
        using var saved = _factory.CreateContext();
        var rows = saved.Habits.ToArray();
        rows.Should().HaveCount(3);
        rows.Should().ContainSingle(habit => habit.Title == title && habit.Id == existing.Id);
        var original = rows.Single(habit => habit.Id == existing.Id);
        original.Description.Should().Be("Keep this description");
        original.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        original.NextDueDate.Should().Be(today);
        var proposed = arguments.GetProperty("habits")[1];
        var stretching = rows.Single(habit => habit.Title == proposed.GetProperty("title").GetString());
        stretching.NextDueDate.Should().Be(today);
        stretching.DueTime.Should().Be(new TimeOnly(8, 0));
        stretching.Schedule.Should().Be(HabitSchedule.Recurring);
        _pending.GetAwaiting(_userId).Should().BeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BrainDump_GateNineIdeaApprovalPreservesEveryStoredFixture(int repeat)
    {
        var today = new DateOnly(2026, 10, 29);
        _provider.GetRequiredService<IUserDateService>().GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        foreach (var title in new[] { "Water", "Walk" })
            _factory.Context.Habits.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
                _userId, title, FrequencyUnit.Day, 1, today)).Value);
        await _factory.Context.SaveChangesAsync();
        using var baseline = _factory.CreateContext();
        var before = baseline.Habits.ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "BrainDumps", $"g9a-brain-dump-ideas-en-r{repeat}.json")));
        var arguments = fixture.RootElement.GetProperty("toolCalls").EnumerateArray()
            .Single(call => call.GetProperty("name").GetString() == "bulk_create_habits").GetProperty("arguments");
        var request = await ConfirmAsync(arguments);
        using (var held = _factory.CreateContext())
            held.Habits.ToDictionary(habit => habit.Id, habit => JsonSerializer.Serialize(habit)).Should().BeEquivalentTo(before);

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, _lastAudit?.Error);
        using var saved = _factory.CreateContext();
        var rows = saved.Habits.ToArray();
        rows.Should().HaveCount(5);
        foreach (var (id, snapshot) in before)
            JsonSerializer.Serialize(rows.Single(habit => habit.Id == id)).Should().Be(snapshot);
        rows.GroupBy(habit => habit.Title).Should().OnlyContain(group => group.Count() == 1);
        rows.Single(habit => habit.Title == "Stretch").NextDueDate.Should().Be(today);
        rows.Single(habit => habit.Title == "Stretch").DueTime.Should().Be(new TimeOnly(8, 0));
    }
}
