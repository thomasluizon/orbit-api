using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Calendar.Services;

namespace Orbit.Infrastructure.Services.Calendar;

/// <summary>
/// Registers Infrastructure-internal Google Calendar collaborators that the composition root
/// in Orbit.Api cannot reference directly (the fetcher and its SDK seam are intentionally
/// internal). Keeps the vendor SDK wrapper out of Application's view while still wiring it for DI.
/// </summary>
public static class CalendarInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddGoogleCalendarServices(this IServiceCollection services)
    {
        services.AddScoped<IGoogleCalendarApi, GoogleCalendarApi>();
        services.AddScoped<ICalendarEventFetcher, GoogleCalendarEventFetcher>();
        return services;
    }
}
