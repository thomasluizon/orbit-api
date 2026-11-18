using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// The app list order of <see cref="HabitAppListOrder"/> against the items that the schedule query of the app
/// returns for the same habits, read depth first.
/// </summary>
public sealed class HabitAppListOrderScheduleTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);
    private static readonly DateTime Start = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task OrderAndSameTitlePlaces_EqualTheOrderOfTheScheduleQuery()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "order@example.com").Value;
        db.Users.Add(user);
        Habit Add(string title, int? position, int minutes, Guid? parent = null)
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.General, user.Id, title, null, null, Today,
                ParentHabitId: parent, Position: position, CreatedAtUtc: Start.AddMinutes(minutes))).Value;
            db.Habits.Add(habit);
            return habit;
        }
        var evening = Add("Evening", 2, 0);
        var morning = Add("Morning", 1, 5);
        var noPosition = Add("Water", null, 1);
        Add("Water", 3, 7);
        Add("Water", 3, 7);
        Add("Water", null, 9, morning.Id);
        Add("Water", 2, 3, morning.Id);
        var early = Add("Water", 2, 2, morning.Id);
        Add("Water", 1, 4, morning.Id);
        Add("Water", null, 6, evening.Id);
        Add("Sip", 1, 8, early.Id);
        Add("Water", 2, 10, early.Id);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var habits = new GenericRepository<Habit>(db);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var handler = new GetHabitScheduleQueryHandler(habits, new HabitScheduleLogReader(db), new HabitSchedulePageLoader(db),
            dates, Substitute.For<IUnitOfWork>(), repeatReminderReader: Substitute.For<IRepeatReminderReader>());

        var page = await handler.Handle(new GetHabitScheduleQuery(user.Id, IsGeneral: true, PageSize: 50), default);

        page.IsSuccess.Should().BeTrue(page.Error);
        var listed = page.Value.Items.SelectMany(item => new[] { item.Id }.Concat(item.Children.SelectMany(Flatten))).ToArray();
        var account = await habits.FindAsync(habit => habit.UserId == user.Id);
        listed.Should().HaveCount(12).And.OnlyHaveUniqueItems();
        HabitAppListOrder.Order(account).Select(habit => habit.Id).Should().Equal(listed);
        listed.Last().Should().Be(noPosition.Id, "a habit with no position comes last on its level");
        var places = new SameTitlePlaces(account);
        var waters = listed.Where(id => account.Single(habit => habit.Id == id).Title == "Water").ToArray();
        waters.Should().HaveCount(9);
        foreach (var (id, index) in waters.Select((id, index) => (id, index)))
            places.Find(id).Should().Be(new SameTitlePlace(index + 1, 9,
                account.Single(habit => habit.Id == id).ParentHabitId is { } parent ? account.Single(habit => habit.Id == parent).Title : null));
        places.Find(evening.Id).Should().BeNull("a unique title has no place");
    }

    private static IEnumerable<Guid> Flatten(HabitScheduleChildItem child) =>
        new[] { child.Id }.Concat(child.Children.SelectMany(Flatten));
}
