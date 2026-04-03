using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkLogHabitsToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly BulkLogHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public BulkLogHabitsToolTests()
    {
        _tool = new BulkLogHabitsTool(_habitRepo, _habitLogRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    [Fact]
    public async Task LogMultiple_ReturnsLoggedNames()
    {
        var h1 = CreateHabit("Water");
        var h2 = CreateHabit("Exercise");
        SetupHabitsFound(h1, h2);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{h2.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().Contain("Exercise");
        await _habitLogRepo.Received(2).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SomeNotFound_LogsFoundOnes()
    {
        var h1 = CreateHabit("Water");
        var missingId = Guid.NewGuid();
        SetupHabitsFound(h1); // Only h1 exists

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{missingId}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
    }

    [Fact]
    public async Task AllNotFound_ReturnsError()
    {
        SetupHabitsFound(); // Empty list

        var id1 = Guid.NewGuid();
        var result = await Execute($$$"""{"habit_ids": ["{{{id1}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No habits were logged");
    }

    [Fact]
    public async Task AlreadyLogged_SkipsAlreadyLogged()
    {
        var logged = CreateHabit("Water");
        logged.Log(Today); // Already logged
        var fresh = CreateHabit("Exercise");
        SetupHabitsFound(logged, fresh);

        var result = await Execute($$$"""{"habit_ids": ["{{{logged.Id}}}", "{{{fresh.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Exercise");
        result.EntityName.Should().NotContain("Water");
    }

    [Fact]
    public async Task EmptyIdList_ReturnsError()
    {
        var result = await Execute("""{"habit_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No valid habit IDs");
    }

    [Fact]
    public async Task MissingHabitIds_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids is required");
    }

    [Fact]
    public async Task WithNote_PassesNoteToLogs()
    {
        var h1 = CreateHabit("Water");
        SetupHabitsFound(h1);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}"], "note": "Morning routine"}""");

        result.Success.Should().BeTrue();
        await _habitLogRepo.Received(1).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
    }

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
    }

    private void SetupHabitsFound(params Habit[] habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(habits.ToList());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
