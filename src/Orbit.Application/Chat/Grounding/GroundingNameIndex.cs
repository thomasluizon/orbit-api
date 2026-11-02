namespace Orbit.Application.Chat.Grounding;

/// <summary>Indexes normalized names once and finds every boundary-delimited match, including overlapping names.</summary>
public sealed class GroundingNameIndex
{
    private sealed class Node
    {
        public Dictionary<char, Node> Children { get; } = [];
        public string? Name { get; set; }
    }

    private readonly Node _root = new();

    public GroundingNameIndex(IEnumerable<string> names)
    {
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var normalized = GroundingTokens.NormalizeName(name);
            var node = _root;
            foreach (var character in normalized)
            {
                if (!node.Children.TryGetValue(character, out var child))
                    node.Children[character] = child = new Node();
                node = child;
            }
            node.Name = normalized;
        }
    }

    internal IEnumerable<string> FindIn(string text)
    {
        for (var start = 0; start < text.Length; start++)
        {
            if (start > 0 && IsWord(text[start - 1])) continue;
            var node = _root;
            for (var end = start; end < text.Length; end++)
            {
                if (!node.Children.TryGetValue(text[end], out var next)) break;
                node = next;
                if (node.Name is { } name && (end + 1 == text.Length || !IsWord(text[end + 1])))
                    yield return name;
            }
        }
    }

    private static bool IsWord(char value) => char.IsLetter(value) || char.IsNumber(value);
}
