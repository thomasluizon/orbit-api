using System.Linq.Expressions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Tests.Shared;

internal static class SameTitleDetailWriters
{
    /// <summary>A writer over an account that holds no habit, so it writes no detail.</summary>
    public static SameTitleDetailWriter NoHabits()
    {
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([]);
        return new SameTitleDetailWriter(habits, Substitute.For<IGenericRepository<User>>());
    }

    /// <summary>A writer that reads the habits and the profile language of the database.</summary>
    public static SameTitleDetailWriter Over(OrbitDbContext database) =>
        new(new GenericRepository<Habit>(database), new GenericRepository<User>(database));
}
