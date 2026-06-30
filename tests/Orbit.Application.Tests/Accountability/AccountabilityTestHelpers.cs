using System.Linq.Expressions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Accountability;

/// <summary>
/// Shared arrangement helpers for the accountability handler tests. Repository lookups resolve against
/// an in-memory set by compiling the LINQ predicate. Adds FindTracked stubbing (used by the linked-habit
/// replacement) on top of the read methods the social helpers cover.
/// </summary>
internal static class AccountabilityTestHelpers
{
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

        repository.FindTrackedAsync(
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
