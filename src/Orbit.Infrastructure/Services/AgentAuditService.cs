using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AgentAuditService(OrbitDbContext dbContext) : IAgentAuditService
{
    public async Task RecordAsync(AgentAuditEntry entry, CancellationToken cancellationToken = default)
    {
        var entity = AgentAuditLog.Create(entry);
        await dbContext.AgentAuditLogs.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
