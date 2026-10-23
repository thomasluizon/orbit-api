using System.Net;
using Orbit.Api.Pages;
using Orbit.Domain.Models;

namespace Orbit.Api.OAuth;

public static class OAuthConsentPage
{
    public const string SubmitScript = """
const form = document.getElementById('consent-form');
let pending = false;
form.addEventListener('submit', (event) => {
  if (pending) {
    event.preventDefault();
    return;
  }
  pending = true;
  form.setAttribute('aria-busy', 'true');
  form.querySelectorAll('button[name="access"]').forEach((button) => {
    button.setAttribute('aria-busy', 'true');
    button.setAttribute('aria-disabled', 'true');
  });
});
""";

    public static string RenderCompleted(string language)
    {
        var copy = OAuthPageStrings.For(language);
        return MessagePage.Render(copy.Locale, copy.ConsentCompletedTitle, copy.ConsentCompletedBody);
    }

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
{{OAuthPageHead.Render(title)}}
<body><main class="card"><h1>{{title}}</h1><p>{{description}}</p><ul>{{items}}</ul>
<form id="consent-form" method="post" action="/oauth/consent"><input type="hidden" name="ticket" value="{{WebUtility.HtmlEncode(ticket)}}">
<button class="btn btn-primary" name="access" value="read_only">{{readOnly}}</button>
<button class="btn btn-ghost" name="access" value="requested">{{requested}}</button>
</form></main>
<script>
{{SubmitScript}}
</script>
</body></html>
""";
    }
}
