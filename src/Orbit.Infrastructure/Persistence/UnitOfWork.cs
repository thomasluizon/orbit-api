using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class UnitOfWork(OrbitDbContext context) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await context.SaveChangesAsync(cancellationToken);
    }

    public void Dispose()
    {
        context.Dispose();
        GC.SuppressFinalize(this);
    }
}
