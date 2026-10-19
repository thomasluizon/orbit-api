using System.Net;
using Orbit.Domain.Models;

namespace Orbit.Api.OAuth;

public static class OAuthConsentPage
{
    public static string Render(string ticket, ConsentEntry entry)
    {
        var portuguese = entry.Language == "pt-BR";
        var title = portuguese ? "Permitir acesso ao Orbit" : "Allow access to Orbit";
        var description = portuguese ? "Revise as permissões solicitadas antes de continuar." : "Review the requested permissions before continuing.";
        var readOnly = portuguese ? "Permitir somente leitura" : "Allow read-only access";
        var requested = portuguese ? "Permitir acesso solicitado" : "Allow requested access";
        var items = string.Join("", (entry.Scopes ?? AgentScopes.ClaudeDefaultScopes)
            .Select(scope => $"<li>{WebUtility.HtmlEncode(scope)}</li>"));
        return $$"""
<!DOCTYPE html>
<html lang="{{(portuguese ? "pt-BR" : "en")}}">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="robots" content="noindex"><title>{{title}}</title><style>{{OAuthPageStyles.Css}}</style></head>
<body><main class="card"><h1>{{title}}</h1><p>{{description}}</p><ul>{{items}}</ul>
<form method="post" action="/oauth/consent"><input type="hidden" name="ticket" value="{{WebUtility.HtmlEncode(ticket)}}">
<button class="btn btn-primary" name="access" value="read_only">{{readOnly}}</button>
<button class="btn btn-ghost" name="access" value="requested">{{requested}}</button>
</form></main></body></html>
""";
    }
}
