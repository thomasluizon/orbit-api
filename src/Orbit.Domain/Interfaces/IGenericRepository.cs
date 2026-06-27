using System.Linq.Expressions;
using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IGenericRepository<T> where T : Entity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, Func<IQueryable<T>, IQueryable<T>>? includes, CancellationToken cancellationToken = default);
    Task<T?> FindOneTrackedAsync(Expression<Func<T, bool>> predicate, Func<IQueryable<T>, IQueryable<T>>? includes = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes a tracked entity's property values and concurrency token from the database,
    /// resetting it to Unchanged. Used after a concurrency conflict so a retry re-evaluates its
    /// guard against current state.
    /// </summary>
    Task ReloadAsync(T entity, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> FindTrackedAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> FindTrackedAsync(Expression<Func<T, bool>> predicate, Func<IQueryable<T>, IQueryable<T>>? includes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads tracked entities matching <paramref name="predicate"/> with global query filters disabled,
    /// so soft-deleted rows are included. Restore paths use this to find an entity the normal filtered
    /// queries hide; always constrain the predicate with the owner's id to keep the scope per-user.
    /// </summary>
    Task<IReadOnlyList<T>> FindTrackedIgnoringFiltersAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    void Update(T entity);
    void Remove(T entity);
    void RemoveRange(IEnumerable<T> entities);
}
