using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Queries;

public record CalendarEventItem(
    string Id,
    string Title,
    string? Description,
    string? StartDate,
    string? StartTime,
    string? EndTime,
    bool IsRecurring,
    string? RecurrenceRule,
    List<int> Reminders);

public record GetCalendarEventsQuery(Guid UserId) : IRequest<Result<List<CalendarEventItem>>>;

public partial class GetCalendarEventsQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<GoogleCalendarSyncSuggestion> suggestionRepository,
    IPayGateService payGate,
    IGoogleTokenService googleTokenService,
    ICalendarEventFetcher eventFetcher,
    IUnitOfWork unitOfWork,
    ILogger<GetCalendarEventsQueryHandler> logger) : IRequestHandler<GetCalendarEventsQuery, Result<List<CalendarEventItem>>>
{
    private const string GoogleCalendarReconnectMessage = "Google Calendar connection expired. Please reconnect.";

    public async Task<Result<List<CalendarEventItem>>> Handle(GetCalendarEventsQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<List<CalendarEventItem>>();

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<List<CalendarEventItem>>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var accessToken = await ResolveAccessTokenAsync(user, cancellationToken);
        if (accessToken is null)
            return Result.Failure<List<CalendarEventItem>>("Google Calendar not connected. Please sign in with Google first.");

        try
        {
            var service = CreateCalendarService(accessToken);
            var fetched = await eventFetcher.FetchAsync(service, updatedMin: null, cancellationToken);

            var importedEventIds = await BuildImportedEventIdSet(request.UserId, cancellationToken);
            var items = fetched
                .Where(item => !importedEventIds.Contains(item.Id))
                .ToList();

            return Result.Success(items);
        }
        catch (Google.GoogleApiException ex)
        {
            LogGoogleCalendarApiError(logger, ex, request.UserId);
            if (IsReconnectRequired(ex))
            {
                user.MarkCalendarSyncReconnectRequired(NormalizeGoogleApiErrorCode(ex));
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Failure<List<CalendarEventItem>>(GoogleCalendarReconnectMessage);
            }
            return Result.Failure<List<CalendarEventItem>>("Failed to fetch calendar events. Please try again.");
        }
    }

    private async Task<string?> ResolveAccessTokenAsync(User user, CancellationToken cancellationToken)
    {
        if (user.GoogleRefreshToken is null)
        {
            var existingAccessToken = await googleTokenService.GetValidAccessTokenAsync(user, cancellationToken);
            if (existingAccessToken is not null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return existingAccessToken;
        }

        var refresh = await googleTokenService.TryRefreshAsync(user, cancellationToken);

        if (refresh.Result == GoogleTokenRefreshResult.RefreshTokenInvalid)
        {
            user.MarkCalendarSyncReconnectRequired(refresh.ErrorCode ?? "invalid_grant");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }

        var accessToken = refresh.AccessToken ?? user.GoogleAccessToken;
        if (accessToken is null)
            return null;

        await unitOfWork.SaveChangesAsync(cancellationToken); // Persist refreshed token
        return accessToken;
    }

    private static CalendarService CreateCalendarService(string accessToken)
    {
        var credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(accessToken);
        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Orbit"
        });
    }

    private async Task<HashSet<string>> BuildImportedEventIdSet(Guid userId, CancellationToken ct)
    {
        var habitEventIds = (await habitRepository.FindAsync(
                h => h.UserId == userId && h.GoogleEventId != null, ct))
            .Select(h => h.GoogleEventId!)
            .ToList();

        var pendingSuggestionEventIds = (await suggestionRepository.FindAsync(
                s => s.UserId == userId && s.DismissedAtUtc == null && s.ImportedAtUtc == null, ct))
            .Select(s => s.GoogleEventId)
            .ToList();

        var set = new HashSet<string>(habitEventIds, StringComparer.Ordinal);
        foreach (var id in pendingSuggestionEventIds)
            set.Add(id);
        return set;
    }

    private static bool IsReconnectRequired(Google.GoogleApiException ex)
    {
        var errorText = NormalizeGoogleApiErrorCode(ex);
        var isAuthStatus = ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;

        return isAuthStatus
            || errorText.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("invalid authentication credentials", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGoogleApiErrorCode(Google.GoogleApiException ex)
    {
        var message = ex.Error?.Message;
        if (!string.IsNullOrWhiteSpace(message))
            return message;

        return ex.Message;
    }

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Google Calendar API error for user {UserId}")]
    private static partial void LogGoogleCalendarApiError(ILogger logger, Exception ex, Guid userId);
}
