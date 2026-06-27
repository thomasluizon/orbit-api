using System.Linq.Expressions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

/// <summary>
/// Shared arrangement helpers for the social handler tests. The access guard and friend-graph
/// services are concrete (non-virtual), so the tests construct them for real over substituted
/// repositories whose lookups resolve against an in-memory set by compiling the LINQ predicate.
/// </summary>
internal static class SocialTestHelpers
{
    public static User OptedInUser(string name = "Test User")
    {
        var user = User.Create(name, $"{Guid.NewGuid():N}@example.com").Value;
        user.SeedDefaultHandle();
        user.SetSocialOptIn(true);
        return user;
    }

    public static User OptedOutUser(string name = "Private User")
    {
        var user = User.Create(name, $"{Guid.NewGuid():N}@example.com").Value;
        user.SeedDefaultHandle();
        return user;
    }

    public static void StubUsers(IGenericRepository<User> repository, params User[] users) =>
        StubFind(repository, users);

    public static void StubFind<T>(IGenericRepository<T> repository, params T[] items) where T : Entity
    {
        repository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<T, bool>>>(),
                Arg.Any<Func<IQueryable<T>, IQueryable<T>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => items.FirstOrDefault(call.Arg<Expression<Func<T, bool>>>().Compile()));

        repository.FindAsync(
                Arg.Any<Expression<Func<T, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<T>)items
                .Where(call.Arg<Expression<Func<T, bool>>>().Compile())
                .ToList());

        repository.AnyAsync(
                Arg.Any<Expression<Func<T, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => items.Any(call.Arg<Expression<Func<T, bool>>>().Compile()));

        repository.CountAsync(
                Arg.Any<Expression<Func<T, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => items.Count(call.Arg<Expression<Func<T, bool>>>().Compile()));
    }
}
