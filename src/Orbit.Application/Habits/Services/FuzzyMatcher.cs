namespace Orbit.Application.Habits.Services;

public static class FuzzyMatcher
{
    public static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    public static bool FuzzyContains(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return false;

        // Fast path: exact substring match
        if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            return true;

        // Short terms: exact only (avoid false positives)
        if (term.Length <= 2)
            return false;

        var searchWords = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var textWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var searchWord in searchWords)
        {
            var maxDistance = searchWord.Length >= 8 ? 2 : 1;
            var wordMatched = false;
            foreach (var textWord in textWords)
            {
                if (Math.Abs(searchWord.Length - textWord.Length) > maxDistance)
                    continue;
                if (LevenshteinDistance(searchWord, textWord) <= maxDistance)
                {
                    wordMatched = true;
                    break;
                }
            }
            if (!wordMatched) return false;
        }

        return true;
    }
}
