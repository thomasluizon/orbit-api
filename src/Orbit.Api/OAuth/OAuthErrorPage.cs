namespace Orbit.Api.OAuth;

internal static class OAuthErrorPage
{
    internal static string Render(string language)
    {
        var copy = OAuthPageStrings.For(language);
        return $$"""
<!DOCTYPE html>
<html lang="{{copy.Locale}}">
{{OAuthPageHead.Render(copy.InvalidRequestTitle)}}
<body>
<main class="card">
{{OAuthPageStyles.Mark}}
<div class="intro">
<h1>{{copy.InvalidRequestTitle}}</h1>
<p class="body-line">{{copy.InvalidRequestBody}}</p>
</div>
</main>
</body>
</html>
""";
    }
}
