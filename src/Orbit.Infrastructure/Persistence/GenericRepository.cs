using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class GenericRepository<T>(OrbitDbContext context) : IGenericRepository<T> where T : Entity
{
    private readonly DbSet<T> _dbSet = context.Set<T>();

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync([id], cancellationToken);
    }

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<T>>? includes,
        CancellationToken cancellationToken = default)
    {
        IQueryable<T> query = _dbSet.AsNoTracking();
        if (includes is not null)
            query = includes(query).AsSplitQuery();
        return await query.Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<T?> FindOneTrackedAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<T>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<T> query = _dbSet;
        if (includes is not null)
            query = includes(query).AsSplitQuery();
        return await query.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public async Task ReloadAsync(T entity, CancellationToken cancellationToken = default)
    {
        await context.Entry(entity).ReloadAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindTrackedAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindTrackedAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<T>>? includes,
        CancellationToken cancellationToken = default)
    {
        IQueryable<T> query = _dbSet;
        if (includes is not null)
            query = includes(query).AsSplitQuery();
        return await query.Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindTrackedIgnoringFiltersAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.IgnoreQueryFilters().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<T?> FindOneTrackedIgnoringFiltersAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.IgnoreQueryFilters().FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.CountAsync(predicate, cancellationToken);
    }

    public async Task<int> SumAsync(
        Expression<Func<T, bool>> predicate,
        Expression<Func<T, int>> selector,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.Where(predicate).SumAsync(selector, cancellationToken);
    }

    public async Task<(IReadOnlyList<T> Items, int TotalCount)> FindPagedAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>> orderBy,
        int page,
        int pageSize,
        Func<IQueryable<T>, IQueryable<T>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbSet.AsNoTracking().Where(predicate);
        var totalCount = await query.CountAsync(cancellationToken);

        if (includes is not null)
            query = includes(query);

        var items = await orderBy(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<bool> AnyAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.AnyAsync(predicate, cancellationToken);
    }

    public async Task<bool> AnyIgnoringFiltersAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.IgnoreQueryFilters().AnyAsync(predicate, cancellationToken);
    }

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        await _dbSet.AddAsync(entity, cancellationToken);
    }

    public void Update(T entity)
    {
        _dbSet.Update(entity);
    }

    public void Remove(T entity)
    {
        _dbSet.Remove(entity);
    }

    public void RemoveRange(IEnumerable<T> entities)
    {
        _dbSet.RemoveRange(entities);
    }
}
