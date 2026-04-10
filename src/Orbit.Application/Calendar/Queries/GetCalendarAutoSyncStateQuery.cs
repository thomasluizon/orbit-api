using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Queries;

public record GetCalendarAutoSyncStateQuery(Guid UserId) : IRequest<Result<CalendarAutoSyncStateResponse>>;

public record CalendarAutoSyncStateResponse(
    bool Enabled,
    GoogleCalendarAutoSyncStatus Status,
    DateTime? LastSyncedAt,
    bool HasGoogleConnection);

public class GetCalendarAutoSyncStateQueryHandler(
    IGenericRepository<User> userRepository) : IRequestHandler<GetCalendarAutoSyncStateQuery, Result<CalendarAutoSyncStateResponse>>
{
    public async Task<Result<CalendarAutoSyncStateResponse>> Handle(GetCalendarAutoSyncStateQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<CalendarAutoSyncStateResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var status = user.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle;
        var response = new CalendarAutoSyncStateResponse(
            Enabled: user.GoogleCalendarAutoSyncEnabled,
            Status: status,
            LastSyncedAt: user.GoogleCalendarLastSyncedAt,
            HasGoogleConnection: user.GoogleAccessToken is not null);

        return Result.Success(response);
    }
}
