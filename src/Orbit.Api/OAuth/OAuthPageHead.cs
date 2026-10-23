using System.Net;

namespace Orbit.Api.OAuth;

internal static class OAuthPageHead
{
    internal static string Render(string title) => $$"""
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="dark light">
<meta name="robots" content="noindex">
<title>{{WebUtility.HtmlEncode(title)}}</title>
<link rel="icon" href="/favicon.ico" sizes="16x16 32x32 48x48">
<link rel="icon" href="/favicon.svg" type="image/svg+xml" sizes="any">
<link rel="apple-touch-icon" href="/apple-touch-icon.png" sizes="180x180">
<link rel="preload" href="/oauth/assets/geist-latin.woff2" as="font" type="font/woff2" crossorigin>
<style>
{{OAuthPageStyles.Css}}
</style>
</head>
""";
}
