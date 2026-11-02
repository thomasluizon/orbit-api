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
        IQueryable<T> query = _dbSet.AsNoTracking().Where(predicate);
        if (includes is not null)
            query = includes(query).AsSplitQuery();
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TResult>> ProjectAsync<TResult>(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<TResult>> projection,
        CancellationToken cancellationToken = default)
    {
        return await projection(_dbSet.AsNoTracking().Where(predicate)).ToListAsync(cancellationToken);
    }

    public async IAsyncEnumerable<TResult> StreamAsync<TResult>(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<TResult>> projection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int batchSize = 100;
        var query = projection(_dbSet.AsNoTracking().Where(predicate).OrderBy(entity => entity.Id));
        for (var offset = 0; ; offset += batchSize)
        {
            var count = 0;
            await foreach (var row in query.Skip(offset).Take(batchSize).AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                count++;
                yield return row;
            }
            if (count < batchSize) yield break;
        }
    }

    public async Task<IReadOnlyList<T>> FindIgnoringFiltersAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<T>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<T> query = _dbSet.IgnoreQueryFilters().AsNoTracking().Where(predicate);
        if (includes is not null)
            query = includes(query).AsSplitQuery();
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<T?> FindOneTrackedAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<T>>? includes = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<T> query = _dbSet.Where(predicate);
        if (includes is not null)
            query = includes(query).AsSplitQuery();
        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task ReloadAsync(T entity, CancellationToken cancellationToken = default)
    {
        await context.Entry(entity).ReloadAsync(cancellationToken);
    }

    public async Task<bool> TryRefreshAsync(T entity, CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.DetectChanges();
        var entry = context.Entry(entity);
        if (entry.State is not (EntityState.Unchanged or EntityState.Modified))
            return false;

        var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);
        if (databaseValues is null)
            return false;

        if (entry.State == EntityState.Modified)
            return entity is ITimestamped
                && entry.OriginalValues.GetValue<DateTime>(nameof(ITimestamped.UpdatedAtUtc)).Ticks / 10
                    == databaseValues.GetValue<DateTime>(nameof(ITimestamped.UpdatedAtUtc)).Ticks / 10;

        entry.CurrentValues.SetValues(databaseValues);
        entry.OriginalValues.SetValues(databaseValues);
        return true;
    }

    public async Task ReloadRangeAsync(IReadOnlyCollection<T> entities, CancellationToken cancellationToken = default)
    {
        if (entities.Count == 0) return;
        var ids = entities.Select(entity => entity.Id).ToArray();
        var rows = await _dbSet.IgnoreQueryFilters().AsNoTracking().Where(entity => ids.Contains(entity.Id))
            .ToDictionaryAsync(entity => entity.Id, cancellationToken);
        foreach (var entity in entities)
        {
            var entry = context.Entry(entity);
            if (!rows.TryGetValue(entity.Id, out var row))
            {
                entry.State = EntityState.Detached;
                continue;
            }
            entry.CurrentValues.SetValues(row);
            entry.OriginalValues.SetValues(row);
            entry.State = EntityState.Unchanged;
        }
    }

    public async Task<bool> TryRefreshRangeAsync(IReadOnlyCollection<T> entities, CancellationToken cancellationToken = default)
    {
        if (entities.Count == 0) return true;
        context.ChangeTracker.DetectChanges();
        var ids = entities.Select(entity => entity.Id).ToArray();
        var rows = await _dbSet.AsNoTracking().Where(entity => ids.Contains(entity.Id))
            .ToDictionaryAsync(entity => entity.Id, cancellationToken);
        foreach (var entity in entities)
        {
            var entry = context.Entry(entity);
            if (entry.State is not (EntityState.Unchanged or EntityState.Modified)
                || !rows.TryGetValue(entity.Id, out var row)) return false;
            if (entry.State == EntityState.Modified)
            {
                if (row is not ITimestamped timestamped
                    || entry.OriginalValues.GetValue<DateTime>(nameof(ITimestamped.UpdatedAtUtc)).Ticks / 10
                        != timestamped.UpdatedAtUtc.Ticks / 10) return false;
                continue;
            }
            entry.CurrentValues.SetValues(row);
            entry.OriginalValues.SetValues(row);
        }
        return true;
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
        IQueryable<T> query = _dbSet.Where(predicate);
        if (includes is not null)
            query = includes(query).AsSplitQuery();
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindTrackedIgnoringFiltersAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet.IgnoreQueryFilters().Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindTrackedIgnoringFiltersAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IQueryable<T>> includes,
        CancellationToken cancellationToken = default)
    {
        var query = includes(_dbSet.IgnoreQueryFilters().Where(predicate)).AsSplitQuery();
        return await query.ToListAsync(cancellationToken);
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

    public Task<int> DeleteAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
    {
        return _dbSet.Where(predicate).ExecuteDeleteAsync(cancellationToken);
    }
}
