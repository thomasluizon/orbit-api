namespace Orbit.Application.Notifications;

public static class NotificationUrls
{
    public const string Home = "/";
    public const string Progress = "/progress";
    public const string Chat = "/chat";
    public const string Profile = "/profile";
    public const string CalendarSync = "/calendar-sync";
    public const string CalendarSyncReview = "/calendar-sync?mode=review";

    public static string WrappedClosedMonth(int year, int month) =>
        $"/progress?wrapped=month&year={year}&month={month}";
}
