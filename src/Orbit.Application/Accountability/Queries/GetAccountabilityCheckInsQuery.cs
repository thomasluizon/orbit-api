using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Queries;

public record AccountabilityCheckInDto(
    Guid Id,
    Guid UserId,
    string Handle,
    string DisplayName,
    DateOnly Date,
    string? Note,
    DateTime CreatedAtUtc);

public record AccountabilityCheckInsPage(IReadOnlyList<AccountabilityCheckInDto> Items);

public record GetAccountabilityCheckInsQuery(Guid UserId, Guid PairId) : IRequest<Result<AccountabilityCheckInsPage>>;

public class GetAccountabilityCheckInsQueryHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<AccountabilityPair> pairRepository,
    IGenericRepository<AccountabilityCheckIn> checkInRepository,
    IGenericRepository<User> userRepository) : IRequestHandler<GetAccountabilityCheckInsQuery, Result<AccountabilityCheckInsPage>>
{
    public async Task<Result<AccountabilityCheckInsPage>> Handle(GetAccountabilityCheckInsQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<AccountabilityCheckInsPage>();

        var isParticipant = await pairRepository.AnyAsync(
            p => p.Id == request.PairId && (p.RequesterId == request.UserId || p.AddresseeId == request.UserId),
            cancellationToken);
        if (!isParticipant)
            return Result.Failure<AccountabilityCheckInsPage>(ErrorMessages.PairNotFound);

        var checkIns = await checkInRepository.FindAsync(c => c.PairId == request.PairId, cancellationToken);

        var userIds = checkIns.Select(c => c.UserId).ToHashSet();
        var users = await userRepository.FindAsync(u => userIds.Contains(u.Id), cancellationToken);
        var usersById = users.ToDictionary(u => u.Id);

        var items = checkIns
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.CreatedAtUtc)
            .Select(c =>
            {
                usersById.TryGetValue(c.UserId, out var user);
                return new AccountabilityCheckInDto(
                    c.Id,
                    c.UserId,
                    user?.Handle ?? string.Empty,
                    user?.Name ?? string.Empty,
                    c.Date,
                    c.Note,
                    c.CreatedAtUtc);
            })
            .ToList();

        return Result.Success(new AccountabilityCheckInsPage(items));
    }
}
