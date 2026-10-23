using Orbit.Api.Pages;

namespace Orbit.Api.OAuth;

internal static class OAuthErrorPage
{
    internal static string Render(string language)
    {
        var copy = OAuthPageStrings.For(language);
        return MessagePage.Render(copy.Locale, copy.InvalidRequestTitle, copy.InvalidRequestBody);
    }
}
