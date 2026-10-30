using System.Text.RegularExpressions;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static string ComposeFeatureHelpText(string? text, bool steps)
    {
        var lines = new List<string>();
        var sentences = ShortText(text, int.MaxValue, steps).Split('\n').SelectMany(line => Regex.Split(line,
            @"(?<!\d\.)(?<=[.!?])\s+(?=(?:\*\*|__|[""'“‘(])?\p{Lu})", RegexOptions.None, TimeSpan.FromSeconds(1)));
        foreach (var sentence in sentences)
        {
            var wrapped = WrapHelpSentence(sentence);
            if (wrapped is null || lines.Count + wrapped.Count > 3)
                break;
            lines.AddRange(wrapped);
        }
        return string.Join("\n", lines);
    }

    private static IReadOnlyList<string>? WrapHelpSentence(string sentence)
    {
        if (sentence.Length <= 180) return [sentence];
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 180) return null;
            if (current.Length + word.Length + 1 > 180)
            {
                lines.Add(current);
                current = word;
            }
            else
                current = current.Length == 0 ? word : current + " " + word;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }
}
