using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Application.Habits.Commands;

internal static class BulkHabitCommandFingerprint
{
    public static string Create<TItem>(IReadOnlyList<TItem> items, Func<TItem, DateOnly?> getDate)
        where TItem : IBulkHabitItem
    {
        var value = new StringBuilder();
        foreach (var item in items)
        {
            value.Append(item.HabitId.ToString("N"));
            value.Append(':');
            if (getDate(item) is { } date)
                value.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            value.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }
}
