using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Queries;

public record UserCalendarItem(
    string Id,
    string Name,
    string AccessRole,
    bool Primary,
    string? BackgroundColor,
    bool IsSynced);

public record GetUserCalendarsQuery(Guid UserId) : IRequest<Result<List<UserCalendarItem>>>, IConcurrencyRetryable;

public partial class GetUserCalendarsQueryHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    IGoogleTokenService googleTokenService,
    ICalendarEventFetcher eventFetcher,
    IUnitOfWork unitOfWork,
    ILogger<GetUserCalendarsQueryHandler> logger) : IRequestHandler<GetUserCalendarsQuery, Result<List<UserCalendarItem>>>
{
    public async Task<Result<List<UserCalendarItem>>> Handle(GetUserCalendarsQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<List<UserCalendarItem>>();

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<List<UserCalendarItem>>(ErrorMessages.UserNotFound);

        var accessToken = await googleTokenService.GetValidAccessTokenAsync(user, cancellationToken);
        if (accessToken is null)
            return Result.Failure<List<UserCalendarItem>>(ErrorMessages.CalendarNotConnected);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            var calendars = await eventFetcher.ListCalendarsAsync(accessToken, cancellationToken);
            var selectedIds = user.GetSelectedCalendarIds();
            return Result.Success(MapCalendars(calendars, selectedIds));
        }
        catch (CalendarProviderException ex)
        {
            LogGoogleCalendarApiError(logger, ex, request.UserId);
            if (ex.Kind == CalendarFetchErrorKind.ReconnectRequired)
            {
                user.MarkCalendarSyncReconnectRequired(ex.RawErrorCode ?? "reconnect_required");
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Failure<List<UserCalendarItem>>(ErrorMessages.CalendarReconnectRequired);
            }
            return Result.Failure<List<UserCalendarItem>>(ErrorMessages.CalendarFetchFailed);
        }
    }

    private static List<UserCalendarItem> MapCalendars(
        IReadOnlyList<CalendarListItem> calendars, IReadOnlyList<string>? selectedIds)
    {
        var selected = selectedIds is null
            ? null
            : new HashSet<string>(selectedIds, StringComparer.Ordinal);

        return calendars
            .Select(c => new UserCalendarItem(
                c.Id,
                c.Name,
                c.AccessRole,
                c.Primary,
                c.BackgroundColor,
                selected is null ? c.IsDefaultOwned : selected.Contains(c.Id)))
            .ToList();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Google Calendar list error for user {UserId}")]
    private static partial void LogGoogleCalendarApiError(ILogger logger, Exception ex, Guid userId);
}
