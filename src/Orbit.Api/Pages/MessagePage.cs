using System.Net;
using Orbit.Api.OAuth;

namespace Orbit.Api.Pages;

internal static class MessagePage
{
    internal static string Render(string locale, string title, string message) => $$"""
<!DOCTYPE html>
<html lang="{{WebUtility.HtmlEncode(locale)}}">
{{OAuthPageHead.Render(title)}}
<body>
<main class="card">
{{OAuthPageStyles.Mark}}
<div class="intro">
<h1>{{title}}</h1>
<p class="body-line">{{message}}</p>
</div>
</main>
</body>
</html>
""";
}
