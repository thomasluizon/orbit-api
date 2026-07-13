using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

/// <summary>Groups the repositories the goal write handlers touch to keep their constructors small.</summary>
public record GoalRepositories(
    IGenericRepository<Goal> Goals,
    IGenericRepository<GoalProgressLog> ProgressLogs);
