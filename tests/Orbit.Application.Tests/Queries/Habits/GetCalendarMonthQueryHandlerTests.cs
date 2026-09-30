using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetCalendarMonthQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetCalendarMonthQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);
    private static readonly DateOnly MonthStart = new(2026, 4, 1);
    private static readonly DateOnly MonthEnd = new(2026, 4, 30);

    public GetCalendarMonthQueryHandlerTests()
    {
        _handler = new GetCalendarMonthQueryHandler(_habitRepo, _userDateService, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Habit CreateDailyHabit(string title = "Daily Habit")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    private static Habit CreateOneTimeHabit(string title = "One-time Task")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, null, null,
            DueDate: Today)).Value;
    }

    private static Habit CreateBadHabit(string title = "Bad Habit")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today)).Value;
    }

    [Fact]
    public async Task Handle_ReturnsCalendarData_ForMonth()
    {
        var habit = CreateDailyHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().NotBeNull();
        result.Value.Logs.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_NestedChildren_UseOwnCreationTimestamps()
    {
        var parent = CreateDailyHabit("Parent");
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(-1), ParentHabitId: parent.Id)).Value;
        var grandchild = Habit.Create(new HabitCreateParams(
            UserId, "Grandchild", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(-2), ParentHabitId: child.Id)).Value;
        var parentCreated = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var childCreated = parentCreated.AddDays(1);
        var grandchildCreated = childCreated.AddDays(1);
        var createdAtProperty = typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc), BindingFlags.Instance | BindingFlags.Public)!;
        createdAtProperty.SetValue(parent, parentCreated);
        createdAtProperty.SetValue(child, childCreated);
        createdAtProperty.SetValue(grandchild, grandchildCreated);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child, grandchild }.AsReadOnly());

        var result = await _handler.Handle(new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value.Habits[0], new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var childJson = json.RootElement.GetProperty("children")[0];
        childJson.GetProperty("createdAtUtc").GetDateTime().Should().Be(childCreated);
        childJson.GetProperty("children")[0].GetProperty("createdAtUtc").GetDateTime().Should().Be(grandchildCreated);
    }

    [Fact]
    public async Task Handle_EmptyHabits_ReturnsEmptyResponse()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().BeEmpty();
        result.Value.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_OneTimeTask_IncludedOnDueDate()
    {
        var habit = CreateOneTimeHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_RecurringHabit_ReturnsScheduledDates()
    {
        var loggedDate = new DateOnly(2026, 9, 28);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Caminhar", FrequencyUnit.Week, 3,
            DueDate: loggedDate)).Value;
        habit.Log(loggedDate).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 10, 19));

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var habitItem = result.Value.Habits.Should().ContainSingle().Subject;
        habitItem.Id.Should().Be(habit.Id);
        habitItem.ScheduledDates.Should().Equal(loggedDate);
        result.Value.Logs[habit.Id].Should().ContainSingle(log => log.Date == loggedDate && log.Value == 1);
    }

    [Fact]
    public async Task Handle_DailyHabitLoggedOnLastDay_IncludesDateOnce()
    {
        var loggedDate = new DateOnly(2026, 9, 30);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Daily", FrequencyUnit.Day, 1,
            DueDate: loggedDate)).Value;
        habit.Log(loggedDate).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 10, 1));
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var result = await _handler.Handle(
            new GetCalendarMonthQuery(UserId, new DateOnly(2026, 9, 1), loggedDate),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().ContainSingle()
            .Which.ScheduledDates.Should().Equal(loggedDate);
        result.Value.Logs[habit.Id].Should().ContainSingle(log => log.Date == loggedDate);
    }

    [Fact]
    public async Task Handle_LoggedAndProjectedWeeklyDates_ReturnsSortedUnion()
    {
        var loggedDate = new DateOnly(2026, 9, 7);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Weekly", FrequencyUnit.Week, 1,
            DueDate: loggedDate)).Value;
        habit.Log(loggedDate).IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(new DateOnly(2026, 9, 14));
        habit.Log(new DateOnly(2026, 9, 14), advanceDueDate: false).IsSuccess.Should().BeTrue();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var result = await _handler.Handle(
            new GetCalendarMonthQuery(UserId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().ContainSingle().Which.ScheduledDates.Should().Equal(
            loggedDate,
            new DateOnly(2026, 9, 14),
            new DateOnly(2026, 9, 21),
            new DateOnly(2026, 9, 28));
    }

    [Fact]
    public async Task Handle_LoggedChild_KeepsParentInMonth()
    {
        var loggedDate = new DateOnly(2026, 9, 28);
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Parent", FrequencyUnit.Week, 3,
            DueDate: new DateOnly(2026, 10, 19))).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Week, 3,
            DueDate: loggedDate, ParentHabitId: parent.Id)).Value;
        child.Log(loggedDate).IsSuccess.Should().BeTrue();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child }.AsReadOnly());

        var result = await _handler.Handle(
            new GetCalendarMonthQuery(UserId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var parentItem = result.Value.Habits.Should().ContainSingle().Subject;
        parentItem.Id.Should().Be(parent.Id);
        parentItem.ScheduledDates.Should().BeEmpty();
        var childItem = parentItem.Children.Should().ContainSingle().Subject;
        childItem.Id.Should().Be(child.Id);
        childItem.ScheduledDates.Should().Equal(loggedDate);
        result.Value.Logs.Should().ContainKey(child.Id)
            .WhoseValue.Should().ContainSingle(log => log.Date == loggedDate && log.Value == 1);
    }

    [Theory]
    [InlineData(FrequencyUnit.Week, true, false)]
    [InlineData(null, false, false)]
    [InlineData(FrequencyUnit.Day, false, false)]
    [InlineData(FrequencyUnit.Week, true, true)]
    [InlineData(null, false, true)]
    [InlineData(FrequencyUnit.Day, false, true)]
    public async Task Handle_LoggedFlexibleOrCompletedDescendant_ReturnsExactLogs(
        FrequencyUnit? frequencyUnit, bool isFlexible, bool isGrandchild)
    {
        var loggedDate = MonthEnd.AddDays(-2);
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Parent", null, null, DueDate: MonthEnd.AddDays(1))).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", null, null, DueDate: MonthEnd.AddDays(1), ParentHabitId: parent.Id)).Value;
        var descendant = Habit.Create(new HabitCreateParams(
            UserId, "Logged descendant", frequencyUnit, frequencyUnit.HasValue ? 1 : null,
            DueDate: loggedDate, ParentHabitId: isGrandchild ? child.Id : parent.Id,
            IsFlexible: isFlexible, EndDate: frequencyUnit == FrequencyUnit.Day ? MonthEnd : null)).Value;
        var firstLog = descendant.Log(loggedDate);
        firstLog.IsSuccess.Should().BeTrue();
        var expectedLogs = new List<HabitLog> { firstLog.Value };
        if (frequencyUnit == FrequencyUnit.Day)
        {
            var lastLog = descendant.Log(MonthEnd);
            lastLog.IsSuccess.Should().BeTrue();
            expectedLogs.Insert(0, lastLog.Value);
            descendant.DueDate.Should().Be(MonthEnd.AddDays(1));
            descendant.Update(new HabitUpdateParams(
                descendant.Title, null, frequencyUnit, 1, null, false, MonthStart))
                .IsSuccess.Should().BeTrue();
            descendant.DueDate.Should().Be(MonthStart);
        }
        descendant.IsCompleted.Should().Be(!isFlexible);
        var habits = isGrandchild ? new List<Habit> { parent, child, descendant } : [parent, descendant];
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.AsReadOnly());

        var result = await _handler.Handle(
            new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var parentItem = result.Value.Habits.Should().ContainSingle().Subject;
        parentItem.Id.Should().Be(parent.Id);
        parentItem.ScheduledDates.Should().BeEmpty();
        var descendantItem = parentItem.Children.Should().ContainSingle().Subject;
        if (isGrandchild)
            descendantItem = descendantItem.Children.Should().ContainSingle().Subject;
        descendantItem.Id.Should().Be(descendant.Id);
        descendantItem.IsCompleted.Should().Be(!isFlexible);
        descendantItem.Instances.Should().BeEmpty();
        descendantItem.ScheduledDates.Should().Contain(loggedDate);
        descendantItem.IsLoggedInRange.Should().BeTrue();
        result.Value.Logs[parent.Id].Should().BeEmpty();
        result.Value.Logs.Should().ContainKey(descendant.Id).WhoseValue.Should().Equal(
            expectedLogs.Select(log => new HabitLogResponse(log.Id, log.Date, log.Value, log.CreatedAtUtc)));
    }

    [Fact]
    public async Task Handle_DescendantLogs_PreservesRangeOrderingAndTopLevelLogs()
    {
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Parent", FrequencyUnit.Month, 5, DueDate: MonthStart, IsFlexible: true)).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Month, 5, DueDate: MonthStart,
            ParentHabitId: parent.Id, IsFlexible: true)).Value;
        foreach (var habit in new[] { parent, child })
        {
            habit.Log(MonthStart.AddDays(-1)).IsSuccess.Should().BeTrue();
            habit.Log(MonthStart).IsSuccess.Should().BeTrue();
            habit.SkipFlexible(Today).IsSuccess.Should().BeTrue();
            habit.Log(MonthEnd).IsSuccess.Should().BeTrue();
            habit.Log(MonthEnd.AddDays(1)).IsSuccess.Should().BeTrue();
        }
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child }.AsReadOnly());

        var result = await _handler.Handle(
            new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Logs.Keys.Should().BeEquivalentTo(new[] { parent.Id, child.Id });
        foreach (var habit in new[] { parent, child })
        {
            result.Value.Logs[habit.Id].Should().Equal(habit.Logs
                .Where(log => log.Date >= MonthStart && log.Date <= MonthEnd)
                .OrderByDescending(log => log.Date)
                .Select(log => new HabitLogResponse(log.Id, log.Date, log.Value, log.CreatedAtUtc)));
            result.Value.Logs[habit.Id].Select(log => log.Date).Should().Equal(MonthEnd, Today, MonthStart);
            result.Value.Logs[habit.Id].Select(log => log.Value).Should().Equal(1m, 0m, 1m);
        }
    }

    [Fact]
    public async Task Handle_LoggedChild_KeepsFlexibleParentWithExhaustedTargetInMonth()
    {
        var monthStart = new DateOnly(2026, 9, 1);
        var monthEnd = new DateOnly(2026, 9, 30);
        var loggedDate = new DateOnly(2026, 9, 28);
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Flexible Parent", FrequencyUnit.Year, 1,
            DueDate: new DateOnly(2026, 1, 1), IsFlexible: true)).Value;
        parent.Log(new DateOnly(2026, 8, 31)).IsSuccess.Should().BeTrue();
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Week, 3,
            DueDate: loggedDate, ParentHabitId: parent.Id)).Value;
        var childLog = child.Log(loggedDate).Value;
        child.DueDate.Should().Be(new DateOnly(2026, 10, 19));

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child }.AsReadOnly());

        var result = await _handler.Handle(
            new GetCalendarMonthQuery(UserId, monthStart, monthEnd),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Logs[parent.Id].Should().BeEmpty();
        var parentItem = result.Value.Habits.Should().ContainSingle().Subject;
        parentItem.Id.Should().Be(parent.Id);
        parentItem.ScheduledDates.Should().BeEmpty("an exhausted flexible parent has no occurrence to miss");
        var childItem = parentItem.Children.Should().ContainSingle().Subject;
        childItem.Id.Should().Be(child.Id);
        childItem.ScheduledDates.Should().ContainSingle().Which.Should().Be(loggedDate);
        childItem.Instances.Should().ContainSingle(instance =>
            instance.Date == loggedDate && instance.LogId == childLog.Id);
    }

    [Fact]
    public async Task Handle_HabitWithoutLoggedOrProjectedDates_StaysOutOfMonth()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Future", FrequencyUnit.Week, 3,
            DueDate: new DateOnly(2026, 10, 19))).Value;
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var result = await _handler.Handle(
            new GetCalendarMonthQuery(UserId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_BadHabit_IncludedInResponse()
    {
        var habit = CreateBadHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        if (result.Value.Habits.Count > 0)
            result.Value.Habits[0].IsBadHabit.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CallsAdvanceStaleBadHabitDueDates()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetCalendarMonthQuery(UserId, MonthStart, MonthEnd);

        await _handler.Handle(query, CancellationToken.None);

        await _userDateService.Received(1).GetUserTodayAsync(UserId, Arg.Any<CancellationToken>());
    }
}
