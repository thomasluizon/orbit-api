namespace Orbit.Application.Common;

/// <summary>
/// Resolves whether a user's region uses a 24-hour clock, derived from their IANA
/// time zone. The 12-hour-clock zone set is sourced from the CLDR regions that default
/// to a 12-hour clock (the Americas' English/Spanish locales, Australia, New Zealand,
/// South and Southeast Asia, and a few others); every other zone, and a null or unknown
/// value, resolves to 24-hour, which is the global majority.
/// </summary>
public static class TimeFormatResolver
{
    private static readonly HashSet<string> TwelveHourTimeZones = new(StringComparer.Ordinal)
    {
        "America/New_York", "America/Detroit", "America/Kentucky/Louisville", "America/Kentucky/Monticello",
        "America/Indiana/Indianapolis", "America/Indiana/Vincennes", "America/Indiana/Winamac",
        "America/Indiana/Marengo", "America/Indiana/Petersburg", "America/Indiana/Vevay",
        "America/Chicago", "America/Indiana/Tell_City", "America/Indiana/Knox", "America/Menominee",
        "America/North_Dakota/Center", "America/North_Dakota/New_Salem", "America/North_Dakota/Beulah",
        "America/Denver", "America/Boise", "America/Phoenix", "America/Los_Angeles", "America/Anchorage",
        "America/Juneau", "America/Sitka", "America/Metlakatla", "America/Yakutat", "America/Nome",
        "America/Adak", "Pacific/Honolulu",
        "America/St_Johns", "America/Halifax", "America/Glace_Bay", "America/Moncton", "America/Goose_Bay",
        "America/Toronto", "America/Iqaluit", "America/Winnipeg", "America/Resolute", "America/Rankin_Inlet",
        "America/Regina", "America/Swift_Current", "America/Edmonton", "America/Cambridge_Bay",
        "America/Inuvik", "America/Creston", "America/Dawson_Creek", "America/Fort_Nelson",
        "America/Vancouver", "America/Whitehorse", "America/Dawson",
        "Australia/Sydney", "Australia/Melbourne", "Australia/Brisbane", "Australia/Perth",
        "Australia/Adelaide", "Australia/Hobart", "Australia/Darwin", "Australia/Lord_Howe",
        "Australia/Lindeman", "Australia/Broken_Hill", "Australia/Eucla",
        "Pacific/Auckland", "Pacific/Chatham",
        "Asia/Kolkata", "Asia/Karachi", "Asia/Dhaka", "Asia/Colombo", "Asia/Kathmandu",
        "Asia/Manila", "Asia/Kuala_Lumpur", "Asia/Kuching",
        "Africa/Cairo", "Asia/Riyadh", "Asia/Amman",
        "America/Mexico_City", "America/Cancun", "America/Merida", "America/Monterrey", "America/Matamoros",
        "America/Mazatlan", "America/Chihuahua", "America/Ojinaga", "America/Hermosillo", "America/Tijuana",
        "America/Bahia_Banderas",
        "America/Bogota", "America/El_Salvador", "America/Tegucigalpa", "America/Managua",
        "America/Guatemala", "America/Costa_Rica", "America/Panama", "America/Santo_Domingo",
        "America/Puerto_Rico"
    };

    /// <summary>
    /// Returns true when the region for <paramref name="ianaTimeZone"/> uses a 24-hour
    /// clock. A null or unrecognized zone resolves to 24-hour.
    /// </summary>
    public static bool Uses24HourClock(string? ianaTimeZone) =>
        ianaTimeZone is null || !TwelveHourTimeZones.Contains(ianaTimeZone);
}
