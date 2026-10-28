using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Theory]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public async Task NextReminder_AllReadSurfacesIncludeChildrenAndGrandchildren(HabitSchedule schedule)
    {
        var db = CreateSqliteDbContext();
        var (root, start) = await SeedRepeat(schedule: schedule);
        Habit Child(Guid parentId, int interval) => Habit.Create(new HabitCreateParams(schedule, root.UserId, "Child",
            schedule == HabitSchedule.General ? null : FrequencyUnit.Day,
            schedule == HabitSchedule.General ? null : 1, UtcToday, ParentHabitId: parentId,
            ReminderEnabled: true, RepeatReminder: new(interval, StartAtUtc: start))).Value;
        var child = Child(root.Id, 240);
        var grandchild = Child(child.Id, 120);
        db.Habits.AddRange(child, grandchild);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var dates = ReadDates(root.UserId, start.AddHours(1));
        var reader = new RepeatReminderReader(db, dates, new MutableTimeProvider(new(start.AddHours(1))));
        var habits = new GenericRepository<Habit>(db);
        var logs = new GenericRepository<HabitLog>(db);
        var detail = await new GetHabitByIdQueryHandler(habits, logs, dates, reader).Handle(new(root.UserId, root.Id), default);
        AssertTree(detail.Value);
        var full = await new GetHabitFullDetailQueryHandler(habits, logs, new GenericRepository<User>(db), dates, reader)
            .Handle(new(root.UserId, root.Id), default);
        AssertTree(full.Value.Habit);
        var scheduleResult = await new GetHabitScheduleQueryHandler(habits, new HabitScheduleLogReader(db),
            new HabitSchedulePageLoader(db), dates, Substitute.For<IUnitOfWork>(), reader)
            .Handle(new(root.UserId, UtcToday, UtcToday, IsGeneral: schedule == HabitSchedule.General ? true : null), default);
        AssertTree(scheduleResult.Value.Items.Single());
        if (schedule != HabitSchedule.General)
        {
            var calendar = await new GetCalendarMonthQueryHandler(habits, dates, Substitute.For<IUnitOfWork>(), reader)
                .Handle(new(root.UserId, UtcToday, UtcToday), default);
            AssertTree(calendar.Value.Habits.Single());
        }
        void AssertTree(object response)
        {
            var json = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            json.GetProperty("nextReminderAtUtc").GetDateTime().Should().Be(start.AddHours(8));
            var childJson = json.GetProperty("children").EnumerateArray().Single();
            childJson.GetProperty("nextReminderAtUtc").GetDateTime().Should().Be(start.AddHours(4));
            childJson.GetProperty("children").EnumerateArray().Single().GetProperty("nextReminderAtUtc")
                .GetDateTime().Should().Be(start.AddHours(2));
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("disabled")]
    [InlineData("ended")]
    public async Task NextReminder_UnscheduledRepeatReturnsExplicitNull(string state)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var start = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Water", FrequencyUnit.Day, 1,
            UtcToday.AddDays(-1), ReminderEnabled: state != "disabled", EndDate: state == "ended" ? UtcToday.AddDays(-1) : null,
            RepeatReminder: state == "none" ? null : new(480, StartAtUtc: start))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        (await ReadNextReminder(habit, new MutableTimeProvider(new(start)))).Should().BeNull();
    }

    [Theory]
    [InlineData(30, false, 60)]
    [InlineData(60, true, 120)]
    public async Task NextReminder_SubHourOrConsumedReminderSkipsFollowUp(int interval, bool consumed, int nextMinutes)
    {
        var db = CreateSqliteDbContext();
        var start = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
        var (habit, _) = await SeedRepeat(new(interval, StartAtUtc: start));
        var clock = new MutableTimeProvider(new(start.AddMinutes(interval)));
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        if (consumed)
        {
            (await db.SentReminders.SingleAsync()).TryConsume(clock.GetUtcNow().UtcDateTime).Should().BeTrue();
            await db.SaveChangesAsync();
        }
        (await ReadNextReminder(habit, clock)).Should().Be(start.AddMinutes(nextMinutes));
    }

    [Theory]
    [InlineData(3, 14, 6, 30, 2, 30, 4, 0, 7, 0)]
    [InlineData(11, 7, 6, 0, 1, 30, 1, 45, 6, 30)]
    public async Task NextReminder_DstWindowGapAndOverlapMatchDelivery(int month, int day, int hour, int minute,
        int startHour, int startMinute, int endHour, int endMinute, int expectedHour, int expectedMinute)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone("America/New_York");
        var start = new DateTime(2027, month, day, hour, minute, 0, DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Water", FrequencyUnit.Day, 1,
            new(2027, month, day), ReminderEnabled: true,
            RepeatReminder: new(15, new(startHour, startMinute), new(endHour, endMinute), start))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(start));
        var expected = new DateTime(2027, month, day, expectedHour, expectedMinute, 0, DateTimeKind.Utc);
        (await ReadNextReminder(habit, clock)).Should().Be(expected);
        clock.SetUtcNow(new(expected));
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextReminder_ClipsFollowUpAtTheActiveWindowAndLocalEndDate(bool lastDay)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone("America/Sao_Paulo");
        var dueAtUtc = lastDay
            ? UtcToday.AddDays(1).ToDateTime(new(2, 50), DateTimeKind.Utc)
            : UtcToday.ToDateTime(new(12, 50), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Water", FrequencyUnit.Day, 1,
            UtcToday, ReminderEnabled: true, EndDate: lastDay ? UtcToday : null,
            RepeatReminder: lastDay ? new(480, StartAtUtc: dueAtUtc.AddHours(-8))
                : new(120, new(8, 0), new(10, 0), dueAtUtc.AddHours(-2)))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(dueAtUtc));
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        clock.SetUtcNow(new(dueAtUtc.AddMinutes(1)));
        (await ReadNextReminder(habit, clock)).Should().Be(lastDay ? null
            : UtcToday.AddDays(1).ToDateTime(new(11, 0), DateTimeKind.Utc));
    }

    [Fact]
    public async Task NextReminder_BatchedReadsDoNotGrowWithHabitCountAndEnforceOwnership()
    {
        async Task<int> ReadCount(int count)
        {
            var reads = new CountingDbCommandInterceptor();
            using var factory = new SqliteOrbitDbContextFactory(reads);
            var db = factory.Context;
            var user = User.Create("Alex", "alex@test.com").Value;
            var start = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
            var habits = Enumerable.Range(0, count).Select(_ => Habit.Create(new HabitCreateParams(HabitSchedule.Flexible,
                user.Id, "Water", FrequencyUnit.Day, 1, UtcToday,
                ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: start))).Value).ToList();
            db.Users.Add(user);
            db.Habits.AddRange(habits);
            db.HabitLogs.AddRange(habits.Select(h => HabitLog.FromScheduleRead(Guid.NewGuid(), h.Id, UtcToday, 1, 0,
                start, start, loggedOnLocalDate: UtcToday)));
            await db.SaveChangesAsync();
            reads.Reset();
            var reader = new RepeatReminderReader(db, ReadDates(user.Id, start), new MutableTimeProvider(new(start)));
            var result = await reader.GetNextAsync(user.Id, habits.Select(h => h.Id).ToList(), default);
            result.Should().HaveCount(count);
            result.Values.Should().OnlyContain(next => next == start.AddHours(8));
            var commandCount = reads.CommandCount;
            (await reader.GetNextAsync(Guid.NewGuid(), habits.Select(h => h.Id).ToList(), default)).Should().BeEmpty();
            return commandCount;
        }
        var single = await ReadCount(1);
        (await ReadCount(20)).Should().Be(single);
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void NextReminder_OpenApiFieldIsOptionalNullableUtcInstant(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "HabitDetailResponse", "HabitChildResponse", "HabitScheduleItem", "HabitScheduleChildItem" })
        {
            var schema = schemas.GetProperty(name);
            schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().NotContain("nextReminderAtUtc");
            var field = schema.GetProperty("properties").GetProperty("nextReminderAtUtc");
            field.GetProperty("type").EnumerateArray().Select(value => value.GetString()).Should().BeEquivalentTo("null", "string");
            field.GetProperty("format").GetString().Should().Be("date-time");
        }
    }

    private static IUserDateService ReadDates(Guid userId, DateTime nowAtUtc)
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(userId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(DateOnly.FromDateTime(nowAtUtc));
        dates.GetUserWeekStartDayAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        return dates;
    }
}
