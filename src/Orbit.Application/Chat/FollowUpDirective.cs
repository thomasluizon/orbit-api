namespace Orbit.Application.Chat;

public static class FollowUpDirective
{
    public const string Marker = "[[orbit:followups]]";

    public static (string? Text, IReadOnlyList<string>? Items) Extract(string? response)
    {
        if (response is null)
            return (null, null);

        var markerIndex = response.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return (response, null);

        var text = response[..markerIndex].TrimEnd();
        var tail = response[(markerIndex + Marker.Length)..];
        var items = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in tail.Split('\n'))
        {
            var item = line.Trim();
            if (item.Length is < 1 or > 60 || item.Contains("[[", StringComparison.Ordinal)
                || item.Contains("http", StringComparison.OrdinalIgnoreCase)
                || item.Contains("www.", StringComparison.OrdinalIgnoreCase)
                || item.Contains("://", StringComparison.Ordinal)
                || !seen.Add(item))
                continue;

            items.Add(item);
            if (items.Count == 3)
                break;
        }

        return (text, items.Count >= 2 ? items : null);
    }
}
