using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Application.Chat;

public record ParsedResponseDirectives(
    string? VisibleText,
    string? CardText,
    IReadOnlyList<string>? FollowUps);

public static class FollowUpDirective
{
    public const string Marker = "[[orbit:followups]]";

    public static (string? Text, IReadOnlyList<string>? Items) Extract(string? response)
    {
        var parsed = Parse(response);
        return (parsed.VisibleText, parsed.FollowUps);
    }

    public static ParsedResponseDirectives Parse(string? response)
    {
        if (response is null)
            return new(null, null, null);

        var parser = new ResponseDirectiveParser();
        parser.Process(response, flush: true);
        return parser.Complete();
    }
}

public sealed class ResponseDirectiveParser
{
    private const string DirectivePrefix = "[[orbit:";
    private static readonly Regex DirectiveRegex = new(@"\[\[orbit:[a-z:]+\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly StringBuilder _visible = new();
    private readonly StringBuilder _followUpTail = new();
    private readonly List<string> _cardDirectives = [];
    private string _pending = string.Empty;
    private bool _inFollowUps;

    public string Process(string? text, bool flush = false)
    {
        _pending += text ?? string.Empty;
        if (_inFollowUps)
        {
            _followUpTail.Append(_pending);
            _pending = string.Empty;
            return string.Empty;
        }

        var emitted = new StringBuilder();
        while (_pending.Length > 0)
        {
            var directiveIndex = _pending.IndexOf(DirectivePrefix, StringComparison.OrdinalIgnoreCase);
            if (directiveIndex >= 0)
            {
                AppendVisible(_pending[..directiveIndex], emitted);
                var closingIndex = _pending.IndexOf("]]", directiveIndex + DirectivePrefix.Length,
                    StringComparison.Ordinal);
                if (closingIndex < 0)
                {
                    _pending = flush ? string.Empty : _pending[directiveIndex..];
                    break;
                }

                var token = _pending[directiveIndex..(closingIndex + 2)];
                _pending = _pending[(closingIndex + 2)..];
                if (token.Equals(FollowUpDirective.Marker, StringComparison.OrdinalIgnoreCase))
                {
                    _inFollowUps = true;
                    _followUpTail.Append(_pending);
                    _pending = string.Empty;
                    break;
                }

                if (DirectiveRegex.IsMatch(token))
                    _cardDirectives.Add(token);
                continue;
            }

            var retainedCharacters = DirectivePrefixSuffixLength(_pending);
            var emittedLength = _pending.Length - retainedCharacters;
            if (emittedLength == 0)
                break;

            AppendVisible(_pending[..emittedLength], emitted);
            _pending = flush ? string.Empty : _pending[emittedLength..];
        }

        if (flush)
            _pending = string.Empty;
        return emitted.ToString();
    }

    public ParsedResponseDirectives Complete()
    {
        var visibleText = _visible.ToString().TrimEnd();
        var tail = _followUpTail.ToString();
        var cardDirectives = _cardDirectives.Concat(DirectiveRegex.Matches(tail)
            .Select(match => match.Value)
            .Where(token => !token.Equals(FollowUpDirective.Marker, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var cardText = cardDirectives.Count == 0
            ? visibleText
            : string.Join('\n', visibleText, string.Join('\n', cardDirectives));

        if (!_inFollowUps)
            return new(visibleText, cardText, null);

        var items = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in tail.Split('\n'))
        {
            var item = DirectiveRegex.Replace(line, string.Empty).Trim();
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

        return new(visibleText, cardText, items.Count >= 2 ? items : null);
    }

    private void AppendVisible(string text, StringBuilder emitted)
    {
        _visible.Append(text);
        emitted.Append(text);
    }

    private static int DirectivePrefixSuffixLength(string text)
    {
        for (var length = Math.Min(text.Length, DirectivePrefix.Length - 1); length > 0; length--)
        {
            if (text.EndsWith(DirectivePrefix[..length], StringComparison.OrdinalIgnoreCase))
                return length;
        }

        return 0;
    }
}
