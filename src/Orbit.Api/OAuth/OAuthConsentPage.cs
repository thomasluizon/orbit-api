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
        var copy = OAuthPageStrings.For(entry.Language);
        var consent = copy.Consent;
        var scopes = entry.Scopes ?? AgentScopes.ClaudeDefaultScopes;
        var groups = string.Join("", scopes.Select(scope => PermissionLabel(scope, consent)).Distinct()
            .Select(label => $"<li>{BrandText(label)}</li>"));
        var items = string.Join("", scopes.Select(scope => $"<li><code>{WebUtility.HtmlEncode(scope)}</code></li>"));
        return $$"""
<!DOCTYPE html>
<html lang="{{copy.Locale}}">
{{OAuthPageHead.Render(consent.Title)}}
<body><main class="card">
{{OAuthPageStyles.Mark}}
<div class="intro"><h1>{{BrandText(consent.Title)}}</h1><p class="body-line">{{WebUtility.HtmlEncode(consent.Body)}}</p></div>
<section class="scopes">
<ul id="permission-groups">{{groups}}</ul>
<details><summary>{{WebUtility.HtmlEncode(consent.Disclosure)}}</summary><ul>{{items}}</ul></details>
</section>
<form id="consent-form" method="post" action="/oauth/consent"><input type="hidden" name="ticket" value="{{WebUtility.HtmlEncode(ticket)}}">
<div class="action-row">
<button class="btn btn-sm btn-ghost" name="access" value="requested">{{WebUtility.HtmlEncode(consent.Requested)}}</button>
<button class="btn btn-sm btn-primary" name="access" value="read_only">{{WebUtility.HtmlEncode(consent.ReadOnly)}}</button>
<button class="btn btn-sm btn-quiet" name="access" value="cancel">{{WebUtility.HtmlEncode(consent.Cancel)}}</button>
</div>
</form></main>
<script>
{{SubmitScript}}
</script>
</body></html>
""";
    }

    private static string BrandText(string value) => WebUtility.HtmlEncode(value)
        .Replace("Orbit", "<span translate=\"no\">Orbit</span>", StringComparison.Ordinal)
        .Replace("Astra", "<span translate=\"no\">Astra</span>", StringComparison.Ordinal);

    private static string PermissionLabel(string scope, OAuthConsentStrings copy) => scope switch
    {
        AgentScopes.CatalogRead or AgentScopes.ReadConfig or AgentScopes.ReadHabits or AgentScopes.ReadGoals
            or AgentScopes.ReadTags or AgentScopes.ReadGamification or AgentScopes.ReadChecklistTemplates
            or AgentScopes.ReadSync => copy.ReadRoutine,
        AgentScopes.WriteHabits or AgentScopes.WriteGoals or AgentScopes.WriteTags
            or AgentScopes.WriteChecklistTemplates or AgentScopes.WriteGamification or AgentScopes.WriteSync => copy.EditRoutine,
        AgentScopes.DeleteHabits or AgentScopes.DeleteGoals or AgentScopes.DeleteTags => copy.DeleteRoutine,
        AgentScopes.ChatInteract => copy.Chat,
        AgentScopes.ReadProfileBasic or AgentScopes.ReadProfileSensitive or AgentScopes.ReadNotifications
            or AgentScopes.ReadSubscriptions => copy.ReadAccount,
        AgentScopes.WriteProfilePreferences or AgentScopes.WriteAiSettings or AgentScopes.WriteNotifications
            or AgentScopes.DeleteNotifications => copy.Preferences,
        AgentScopes.ReadCalendar or AgentScopes.ManageCalendarSync => copy.Calendar,
        AgentScopes.ReadReferrals or AgentScopes.WriteReferrals => copy.Referrals,
        AgentScopes.ReadNotion => copy.ReadNotion,
        AgentScopes.ManageNotion => copy.ManageNotion,
        AgentScopes.ReadApiKeys => copy.ReadKeys,
        AgentScopes.ManageApiKeys => copy.ManageKeys,
        AgentScopes.ManageSubscriptions => copy.Subscription,
        AgentScopes.ManageAccount or AgentScopes.ManageAuth => copy.Account,
        AgentScopes.UploadMedia => copy.Media,
        AgentScopes.WriteSupport => copy.Support,
        AgentScopes.ManageFeatureFlags or AgentScopes.ManageMarketing => copy.Administration,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown consent permission.")
    };
}
