using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Queries;

public record CheerDto(
    Guid Id,
    Guid SenderId,
    Guid RecipientId,
    Guid? HabitId,
    string? Note,
    DateTime CreatedAtUtc,
    string SenderHandle,
    string SenderDisplayName);

public record CheersPage(IReadOnlyList<CheerDto> Items);

public record GetCheersQuery(Guid UserId, string Direction) : IRequest<Result<CheersPage>>;

public class GetCheersQueryHandler(
    SocialAccessGuard socialAccessGuard,
    ISocialGraphReader socialGraphReader,
    IGenericRepository<User> userRepository,
    TimeProvider timeProvider) : IRequestHandler<GetCheersQuery, Result<CheersPage>>
{
    public const string ReceivedDirection = "received";

    public async Task<Result<CheersPage>> Handle(GetCheersQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<CheersPage>();

        var isReceived = string.Equals(request.Direction, ReceivedDirection, StringComparison.OrdinalIgnoreCase);
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-AppConstants.CheersLookbackDays);

        var cheers = await socialGraphReader.ReadCheersPageAsync(
            request.UserId,
            isReceived,
            since,
            AppConstants.MaxCheersReturned,
            cancellationToken);

        var senderIds = cheers.Select(c => c.SenderId).ToHashSet();
        var senders = await userRepository.FindAsync(u => senderIds.Contains(u.Id), cancellationToken);
        var sendersById = senders.ToDictionary(u => u.Id);

        var items = cheers
            .Select(c =>
            {
                sendersById.TryGetValue(c.SenderId, out var sender);
                return new CheerDto(
                    c.Id,
                    c.SenderId,
                    c.RecipientId,
                    c.HabitId,
                    c.Note,
                    c.CreatedAtUtc,
                    sender?.Handle ?? string.Empty,
                    sender?.Name ?? string.Empty);
            })
            .ToList();

        return Result.Success(new CheersPage(items));
    }
}
