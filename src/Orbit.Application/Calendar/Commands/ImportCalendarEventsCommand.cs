using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Calendar.Commands;

public record ImportedHabitItem(Guid Id, string Title);

public record ImportCalendarEventsResult(int Imported, List<ImportedHabitItem> Habits);

public record ImportCalendarEventsCommand(Guid UserId, IReadOnlyList<string> EventIds)
    : IRequest<Result<ImportCalendarEventsResult>>;

public class ImportCalendarEventsCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGoogleTokenService googleTokenService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<ImportCalendarEventsCommand, Result<ImportCalendarEventsResult>>
{
    public async Task<Result<ImportCalendarEventsResult>> Handle(
        ImportCalendarEventsCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<ImportCalendarEventsResult>("User not found");

        var accessToken = await googleTokenService.GetValidAccessTokenAsync(user, cancellationToken);
        if (accessToken is null)
            return Result.Failure<ImportCalendarEventsResult>("Google Calendar not connected.");

        await unitOfWork.SaveChangesAsync(cancellationToken); // Persist refreshed token

        var credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(accessToken);
        var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Orbit"
        });

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var imported = new List<ImportedHabitItem>();

        foreach (var eventId in request.EventIds)
        {
            try
            {
                var ev = await service.Events.Get("primary", eventId).ExecuteAsync(cancellationToken);
                if (ev is null || string.IsNullOrWhiteSpace(ev.Summary)) continue;

                // Parse dates
                DateOnly dueDate;
                TimeOnly? dueTime = null;

                if (ev.Start?.DateTimeDateTimeOffset is not null)
                {
                    var dt = ev.Start.DateTimeDateTimeOffset.Value;
                    dueDate = DateOnly.FromDateTime(dt.DateTime);
                    dueTime = TimeOnly.FromDateTime(dt.DateTime);
                }
                else if (ev.Start?.Date is not null)
                {
                    dueDate = DateOnly.Parse(ev.Start.Date);
                }
                else
                {
                    dueDate = userToday;
                }

                if (dueDate < userToday) dueDate = userToday;

                // Parse recurrence
                FrequencyUnit? freqUnit = null;
                int? freqQty = null;
                IReadOnlyList<DayOfWeek>? days = null;

                var rrule = ev.Recurrence?.FirstOrDefault(r =>
                    r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));

                if (rrule is not null)
                {
                    var parsed = ParseRRule(rrule);
                    freqUnit = parsed.FreqUnit;
                    freqQty = parsed.FreqQty;
                    days = parsed.Days;
                }

                // Parse reminders
                var reminderTimes = new List<int>();
                bool reminderEnabled = false;
                if (ev.Reminders?.Overrides is not null)
                {
                    foreach (var r in ev.Reminders.Overrides)
                    {
                        if (r.Minutes.HasValue)
                            reminderTimes.Add(r.Minutes.Value);
                    }
                    reminderEnabled = reminderTimes.Count > 0;
                }

                var habitResult = Habit.Create(
                    request.UserId,
                    ev.Summary,
                    freqUnit,
                    freqQty,
                    ev.Description,
                    days: days,
                    dueDate: dueDate,
                    dueTime: dueTime,
                    reminderEnabled: reminderEnabled,
                    reminderTimes: reminderTimes.Count > 0 ? reminderTimes : null);

                if (habitResult.IsFailure) continue;

                await habitRepository.AddAsync(habitResult.Value, cancellationToken);
                imported.Add(new ImportedHabitItem(habitResult.Value.Id, ev.Summary));
            }
            catch
            {
                // Skip events that fail to import
                continue;
            }
        }

        if (imported.Count > 0)
            user.MarkCalendarImported();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new ImportCalendarEventsResult(imported.Count, imported));
    }

    private static (FrequencyUnit? FreqUnit, int? FreqQty, IReadOnlyList<DayOfWeek>? Days) ParseRRule(string rrule)
    {
        var parts = rrule.Replace("RRULE:", "").Split(';')
            .Select(p => p.Split('='))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);

        FrequencyUnit? freqUnit = null;
        int? freqQty = 1;
        List<DayOfWeek>? days = null;

        if (parts.TryGetValue("FREQ", out var freq))
        {
            freqUnit = freq.ToUpperInvariant() switch
            {
                "DAILY" => FrequencyUnit.Day,
                "WEEKLY" => FrequencyUnit.Week,
                "MONTHLY" => FrequencyUnit.Month,
                "YEARLY" => FrequencyUnit.Year,
                _ => null
            };
        }

        if (parts.TryGetValue("INTERVAL", out var interval) && int.TryParse(interval, out var qty))
        {
            freqQty = qty;
        }

        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            days = byDay.Split(',').Select(d => d.Trim().ToUpperInvariant() switch
            {
                "MO" => DayOfWeek.Monday,
                "TU" => DayOfWeek.Tuesday,
                "WE" => DayOfWeek.Wednesday,
                "TH" => DayOfWeek.Thursday,
                "FR" => DayOfWeek.Friday,
                "SA" => DayOfWeek.Saturday,
                "SU" => DayOfWeek.Sunday,
                _ => (DayOfWeek?)null
            }).Where(d => d.HasValue).Select(d => d!.Value).ToList();

            // BYDAY with WEEKLY frequency means specific days -> use Day freq with days list
            if (freqUnit == FrequencyUnit.Week && days.Count > 0 && freqQty == 1)
            {
                freqUnit = FrequencyUnit.Day;
            }
        }

        return (freqUnit, freqQty, days?.Count > 0 ? days : null);
    }
}
