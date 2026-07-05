using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// Renders transactional emails from embedded-resource templates by replacing
/// <c>{{token}}</c> placeholders in a single pass, so substituted values are
/// never re-scanned for tokens. An unknown token in a template throws.
/// </summary>
public static partial class EmailTemplateRenderer
{
    private const string CanvasColor = "#020618";
    private const string GradientTopColor = "#22094F";

    private static readonly ConcurrentDictionary<string, string> TemplateCache = new();

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex TokenPattern();

    public static string RenderHtml(string emailName, EmailLayout layout, IReadOnlyDictionary<string, string> tokens)
    {
        var content = ReplaceTokens(LoadTemplate($"{emailName}.html"), tokens);
        return ReplaceTokens(LoadTemplate("Layout.html"), BuildLayoutTokens(layout, content));
    }

    /// <summary>
    /// Wraps already-rendered <paramref name="contentHtml"/> in the shared branded chrome. Unlike
    /// <see cref="RenderHtml"/> the content is inserted verbatim and never scanned for
    /// <c>{{token}}</c> placeholders, so caller-supplied marketing HTML passes through untouched.
    /// </summary>
    public static string RenderLayout(EmailLayout layout, string contentHtml)
        => ReplaceTokens(LoadTemplate("Layout.html"), BuildLayoutTokens(layout, contentHtml));

    private static Dictionary<string, string> BuildLayoutTokens(EmailLayout layout, string content) => new()
    {
        ["lang"] = layout.Lang,
        ["preheader"] = layout.Preheader,
        ["footer"] = layout.Footer,
        ["logoUrl"] = layout.LogoUrl,
        ["headerBgColor"] = layout.GradientHeader ? GradientTopColor : CanvasColor,
        ["headerBackground"] = layout.GradientHeader
            ? $"background: linear-gradient(180deg, {GradientTopColor} 0%, {CanvasColor} 100%);"
            : $"background-color: {CanvasColor};",
        ["content"] = content,
    };

    public static string RenderText(string emailName, IReadOnlyDictionary<string, string> tokens)
        => ReplaceTokens(LoadTemplate($"{emailName}.txt"), tokens);

    private static string ReplaceTokens(string template, IReadOnlyDictionary<string, string> tokens)
        => TokenPattern().Replace(template, match =>
        {
            var tokenName = match.Groups[1].Value;
            return tokens.TryGetValue(tokenName, out var value)
                ? value
                : throw new InvalidOperationException($"Email template token has no value: {tokenName}");
        });

    private static string LoadTemplate(string fileName)
        => TemplateCache.GetOrAdd(fileName, static name =>
        {
            var resourceName = $"Orbit.Infrastructure.Email.Templates.{name}";
            using var stream = typeof(EmailTemplateRenderer).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded email template not found: {resourceName}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
}
