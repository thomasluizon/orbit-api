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

    /// <summary>
    /// Loads a single tracked entity matching <paramref name="predicate"/> with global query filters
    /// disabled, so a soft-deleted or deactivated row is still returned. Auth and subscription-webhook
    /// reconciliation use this to resolve (and mutate) a user the default deactivation filter hides.
    /// </summary>
    Task<T?> FindOneTrackedIgnoringFiltersAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sums <paramref name="selector"/> over the rows matching <paramref name="predicate"/> in the
    /// database, returning 0 when none match. Lets a caller aggregate without materializing the rows.
    /// </summary>
    Task<int> SumAsync(Expression<Func<T, bool>> predicate, Expression<Func<T, int>> selector, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a filtered, ordered page entirely in the database: the predicate, ordering, and
    /// <c>Skip</c>/<c>Take</c> translate to SQL so only one page of rows is materialized, alongside the
    /// total count of predicate-matched rows. <paramref name="orderBy"/> must impose a deterministic
    /// order for stable paging; <paramref name="includes"/> eager-loads navigations on the page.
    /// </summary>
    Task<(IReadOnlyList<T> Items, int TotalCount)> FindPagedAsync(
        Expression<Func<T, bool>> predicate,
        Func<IQueryable<T>, IOrderedQueryable<T>> orderBy,
        int page,
        int pageSize,
        Func<IQueryable<T>, IQueryable<T>>? includes = null,
        CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Existence check with global query filters disabled, so a soft-deleted or deactivated row still
    /// counts. Used by the admin-authorization and purchase-token-uniqueness guards, which must account
    /// for rows the default deactivation filter hides.
    /// </summary>
    Task<bool> AnyIgnoringFiltersAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    void Update(T entity);
    void Remove(T entity);
    void RemoveRange(IEnumerable<T> entities);
}
