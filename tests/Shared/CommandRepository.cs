using System.Linq.Expressions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Tests.Shared;

public static class CommandRepository
{
    public static IGenericRepository<T> TrackAdds<T>(IGenericRepository<T> original) where T : Entity
    {
        var staged = new List<T>();
        var repository = Substitute.For<IGenericRepository<T>>();
        IReadOnlyList<T> Merge(IReadOnlyList<T> values, Expression<Func<T, bool>> predicate) =>
            values.Concat(staged.Where(predicate.Compile())).DistinctBy(entity => entity.Id).ToArray();
        repository.AddAsync(Arg.Any<T>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var entity = call.Arg<T>();
            staged.Add(entity);
            await original.AddAsync(entity, call.Arg<CancellationToken>());
        });
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>()).Returns(async call =>
            Merge((await original.FindAsync(call.Arg<Expression<Func<T, bool>>>(), call.Arg<CancellationToken>()))
                .Concat(await original.FindTrackedAsync(call.Arg<Expression<Func<T, bool>>>(), call.Arg<CancellationToken>())).ToArray(), call.Arg<Expression<Func<T, bool>>>()));
        repository.FindAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<T>>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            Merge(await original.FindAsync(call.Arg<Expression<Func<T, bool>>>(), call.ArgAt<Func<IQueryable<T>, IQueryable<T>>?>(1), call.Arg<CancellationToken>()), call.Arg<Expression<Func<T, bool>>>()));
        repository.FindTrackedAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>()).Returns(async call =>
            Merge(await original.FindTrackedAsync(call.Arg<Expression<Func<T, bool>>>(), call.Arg<CancellationToken>()), call.Arg<Expression<Func<T, bool>>>()));
        repository.FindTrackedAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<T>>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            Merge((await original.FindTrackedAsync(call.Arg<Expression<Func<T, bool>>>(), call.ArgAt<Func<IQueryable<T>, IQueryable<T>>?>(1), call.Arg<CancellationToken>()))
                .Concat(await original.FindTrackedAsync(call.Arg<Expression<Func<T, bool>>>(), call.Arg<CancellationToken>())).ToArray(), call.Arg<Expression<Func<T, bool>>>()));
        repository.FindOneTrackedAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<T>>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var predicate = call.Arg<Expression<Func<T, bool>>>();
            return await original.FindOneTrackedAsync(predicate, call.ArgAt<Func<IQueryable<T>, IQueryable<T>>?>(1), call.Arg<CancellationToken>())
                ?? staged.FirstOrDefault(predicate.Compile())
                ?? (await original.FindTrackedAsync(predicate, call.ArgAt<Func<IQueryable<T>, IQueryable<T>>?>(1), call.Arg<CancellationToken>())).FirstOrDefault(predicate.Compile());
        });
        repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call =>
            await original.GetByIdAsync(call.Arg<Guid>(), call.Arg<CancellationToken>()) ?? staged.FirstOrDefault(entity => entity.Id == call.Arg<Guid>()));
        repository.AnyAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>()).Returns(async call =>
            await original.AnyAsync(call.Arg<Expression<Func<T, bool>>>(), call.Arg<CancellationToken>()) || staged.Any(call.Arg<Expression<Func<T, bool>>>().Compile()));
        repository.CountAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<CancellationToken>()).Returns(call =>
            original.CountAsync(call.Arg<Expression<Func<T, bool>>>(), call.Arg<CancellationToken>()));
        repository.ReloadAsync(Arg.Any<T>(), Arg.Any<CancellationToken>()).Returns(call => original.ReloadAsync(call.Arg<T>(), call.Arg<CancellationToken>()));
        return repository;
    }
}
