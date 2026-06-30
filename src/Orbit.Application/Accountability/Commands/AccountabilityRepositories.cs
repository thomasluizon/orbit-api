using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Commands;

/// <summary>Groups the repositories the accountability handlers touch to keep their constructors small.</summary>
public record AccountabilityRepositories(
    IGenericRepository<User> Users,
    IGenericRepository<AccountabilityPair> Pairs,
    IGenericRepository<AccountabilityCheckIn> CheckIns,
    IGenericRepository<UserAchievement> Achievements);
