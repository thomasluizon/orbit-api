using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("America/Sao_Paulo", "2026-10-29", false)]
    [InlineData("Pacific/Auckland", "2026-10-30", false)]
    [InlineData("America/Sao_Paulo", null, true)]
    public async Task G11_CapturedStreakCarriesFirstActiveHabitDateInTheAccountTimeZone(
        string zoneId, string? expectedDate, bool noActiveHabits)
    {
        SetupExistingParents("en");
        EnableComposition();
        var day = new DateOnly(2026, 10, 30);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(day);
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.FindSystemTimeZoneById(zoneId));
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "CompositionFailures", "prod-r1-composition-streaks-en-r2.json")));
        var habits = capture.RootElement.GetProperty("groundingEvidence").GetProperty("fixture").GetProperty("habits")
            .EnumerateArray().Select(item => Habit.Create(new HabitCreateParams(HabitSchedule.OneTime,
                UserId, item.GetProperty("title").GetString()!, null, null, day,
                CreatedAtUtc: DateTime.Parse(item.GetProperty("createdAtUtc").GetString()!, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal))).Value).ToList();
        if (noActiveHabits)
            habits.ForEach(habit => habit.Log(day));
        var completed = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId,
            "Completed task", null, null, day, CreatedAtUtc: DateTime.UnixEpoch)).Value;
        completed.Log(day);
        habits.Add(completed);
        var deleted = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId,
            "Deleted task", null, null, day, CreatedAtUtc: DateTime.UnixEpoch)).Value;
        deleted.SoftDelete();
        habits.Add(deleted);
        habits.Add(Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, Guid.NewGuid(),
            "Another account", null, null, day, CreatedAtUtc: DateTime.UnixEpoch)).Value);
        _habitRepo.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<DateTime>>>(), Arg.Any<CancellationToken>()).Returns(call =>
                (IReadOnlyList<DateTime>)call.Arg<Func<IQueryable<Habit>, IQueryable<DateTime>>>()(
                    habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).AsQueryable()).ToArray());
        GamificationReadTestData.Configure(_mediator, day);
        var tool = new GetGamificationOverviewTool(_mediator, _habitRepo, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(CapturedCompositionPlan("prod-r1-composition-streaks-en-r2"));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["streak"], SupportsStreakCard: true, Locale: "en");
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var card = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject.Block
            .Should().BeOfType<StreakCard>().Subject;
        var json = JsonSerializer.SerializeToElement(card, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.TryGetProperty("firstEligibleDate", out var first).Should().Be(!noActiveHabits);
        if (!noActiveHabits) first.GetString().Should().Be(expectedDate);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta" && !string.IsNullOrWhiteSpace(item.Text));
        AssertStoredComposition(result.Value, client, "en");
        await AssertNoCompositionContinuation();
    }
}
