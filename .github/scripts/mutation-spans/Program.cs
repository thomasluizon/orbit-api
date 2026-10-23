using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

var inputs = JsonSerializer.Deserialize<Input[]>(Console.In.ReadToEnd())
    ?? throw new InvalidDataException("Expected mutation span inputs");
var results = inputs.Select(input =>
{
    var before = SourceText.From(input.Before);
    var after = SourceText.From(input.After);
    var oldTokens = CSharpSyntaxTree.ParseText(before).GetRoot().DescendantTokens().ToArray();
    var newTokens = CSharpSyntaxTree.ParseText(after).GetRoot().DescendantTokens().ToArray();
    var spans = new List<int[]>();
    foreach (var hunk in input.Hunks)
    {
        if (hunk.NewCount == 0) continue;
        var oldSpan = Lines(before, hunk.OldStart, hunk.OldCount);
        var newSpan = Lines(after, hunk.NewStart, hunk.NewCount);
        var oldCode = oldTokens.Where(token => token.Span.OverlapsWith(oldSpan))
            .Select(token => (token.RawKind, token.Text));
        var newCode = newTokens.Where(token => token.Span.OverlapsWith(newSpan))
            .Select(token => (token.RawKind, token.Text));
        if (oldCode.SequenceEqual(newCode)) continue;
        var enclosingSpans = newTokens.Where(token => token.Span.OverlapsWith(newSpan))
            .SelectMany(token => token.Parent?.AncestorsAndSelf() ?? [])
            .Where(node => node is ExpressionSyntax or StatementSyntax)
            .Select(node => node.Span)
            .Append(newSpan);
        spans.Add([enclosingSpans.Min(span => span.Start), enclosingSpans.Max(span => span.End)]);
    }
    return spans;
});
Console.Write(JsonSerializer.Serialize(results));

static TextSpan Lines(SourceText text, int start, int count)
{
    if (count == 0) return new TextSpan(0, 0);
    return TextSpan.FromBounds(text.Lines[start - 1].Start, text.Lines[start + count - 2].EndIncludingLineBreak);
}

record Input(string Before, string After, Hunk[] Hunks);
record Hunk(int OldStart, int OldCount, int NewStart, int NewCount);
