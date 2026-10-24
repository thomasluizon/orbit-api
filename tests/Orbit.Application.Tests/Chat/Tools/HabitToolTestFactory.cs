using System.Linq.Expressions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

internal static class HabitToolTestFactory
{
    public static Habit CreateHabit(Guid userId, string title) =>
        Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId, title, FrequencyUnit.Day, 1, new DateOnly(2026, 8, 6))).Value;

    public static void SetupHabitFound(IGenericRepository<Habit> repository, Habit habit)
    {
        repository.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Expression<Func<Habit, bool>>>().Compile()(habit) ? habit : null);
        repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Expression<Func<Habit, bool>>>().Compile()(habit) ? [habit] : []);
    }
}
