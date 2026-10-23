namespace Orbit.Api.OAuth;

/// <summary>
/// The MCP authorize page. It renders both steps of the email path plus the Google path, in the language the
/// request asked for, against the design system's own tokens.
/// </summary>
public static class OAuthLoginPage
{
    private const string Cell = """<span class="cell" data-cell aria-hidden="true"></span>""";

    public static string Render(string clientId, string redirectUri, string state,
        string codeChallenge, string codeChallengeMethod, string? nonce = null,
        string? error = null, string language = "en",
        bool botProtectionEnabled = false, string? turnstileSiteKey = null, string? scope = null, bool appleSignInEnabled = false)
    {
        var copy = OAuthPageStrings.For(language);
        var requestJson = OAuthPageJson.Write(new
        {
            clientId,
            redirectUri,
            state,
            codeChallenge,
            codeChallengeMethod,
            nonce,
            scope,
            language = copy.Locale,
            error,
            botProtectionEnabled,
            turnstileSiteKey = botProtectionEnabled ? turnstileSiteKey : null
        });
        var copyJson = OAuthPageJson.Write(new
        {
            emailTitle = copy.EmailTitle,
            emailBody = copy.EmailBody,
            emailInvalid = copy.EmailInvalid,
            sendFailed = copy.SendFailed,
            codeTitle = copy.CodeTitle,
            sentToLine = copy.SentToLine,
            codeSent = copy.CodeSent,
            codeResent = copy.CodeResent,
            codeHint = copy.CodeHint,
            verifyFailed = copy.VerifyFailed,
            resendCountdown = copy.ResendCountdown,
            authorizedLine = copy.AuthorizedLine
        });

        var turnstileWidget = botProtectionEnabled ? """<div id="turnstile-widget"></div>""" : "";
        var turnstileScript = botProtectionEnabled
            ? """<script src="https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit&amp;onload=onOAuthTurnstileLoad" defer onerror="onOAuthTurnstileError()"></script>"""
            : "";

        var googleStartQuery = $"client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}"
            + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
            + $"&code_challenge_method={Uri.EscapeDataString(codeChallengeMethod)}";
        if (nonce is not null)
            googleStartQuery += $"&nonce={Uri.EscapeDataString(nonce)}";
        if (scope is not null) googleStartQuery += $"&scope={Uri.EscapeDataString(scope)}";
        var googleStartUrl = System.Net.WebUtility.HtmlEncode($"/oauth/google/start?{googleStartQuery}");

        var appleStartUrl = System.Net.WebUtility.HtmlEncode($"/oauth/apple/start?{googleStartQuery}");
        var appleLabel = copy.Locale == "pt-BR" ? "Iniciar sessão com a Apple" : "Sign in with Apple";
        var appleButton = appleSignInEnabled
            ? $"<a class=\"btn btn-ghost\" id=\"apple\" href=\"{appleStartUrl}\">{appleLabel}</a>" : "";

        return $$"""
<!DOCTYPE html>
<html lang="{{copy.Locale}}">
{{OAuthPageHead.Render(copy.DocumentTitle)}}
<body>
<main class="card">
{{OAuthPageStyles.Mark}}
<div class="intro">
<p class="eyebrow" translate="no">{{copy.Eyebrow}}</p>
<h1 id="step-title">{{copy.EmailTitle}}</h1>
<p class="body-line" id="step-body">{{copy.EmailBody}}</p>
<p class="note hidden" id="status" role="status"></p>
</div>
<p class="alert hidden" id="error" role="alert"></p>
{{turnstileWidget}}
<section class="step" id="step-email">
<div class="field" id="email-field">
<label for="email">{{copy.EmailLabel}}</label>
<div class="control"><input type="email" id="email" name="email" placeholder="{{copy.EmailPlaceholder}}" autocomplete="email" spellcheck="false" autofocus></div>
<span class="caption hidden" id="email-error"></span>
</div>
<button type="button" class="btn btn-primary" id="send-code">{{copy.ContinueLabel}}</button>
<div class="divider" aria-hidden="true"><span data-rule></span><span data-label>{{copy.OrLabel}}</span><span data-rule></span></div>
<a class="btn btn-ghost btn-google" id="google" href="{{googleStartUrl}}"><img src="/oauth/assets/google-mark.svg" width="20" height="20" alt="" aria-hidden="true">{{copy.GoogleLabel}}</a>
{{appleButton}}
</section>
<section class="step hidden" id="step-code">
<button type="button" class="btn btn-ghost btn-sm" id="change-email">{{copy.ChangeEmailLabel}}</button>
<div class="field">
<div class="code" id="code">
<input id="code-input" type="text" inputmode="numeric" autocomplete="one-time-code" maxlength="6" spellcheck="false" aria-label="{{copy.CodeLabel}}" aria-describedby="code-caption">
{{Cell}}{{Cell}}{{Cell}}{{Cell}}{{Cell}}{{Cell}}
</div>
<span class="note" id="code-caption">{{copy.CodeHint}}</span>
</div>
<button type="button" class="btn btn-primary" id="verify">{{copy.VerifyLabel}}</button>
<div class="actions">
<button type="button" class="btn btn-ghost btn-sm hidden" id="resend">{{copy.ResendLabel}}</button>
<p class="note hidden" id="resend-countdown" data-tabular></p>
</div>
</section>
<section class="scopes">
<p>{{copy.ScopeTitle}}</p>
<ul>
<li>{{copy.ScopeHabits}}</li>
<li>{{copy.ScopeProfile}}</li>
<li>{{copy.ScopeProgress}}</li>
</ul>
</section>
</main>
<script type="application/json" id="oauth-request">{{requestJson}}</script>
<script type="application/json" id="oauth-copy">{{copyJson}}</script>
<script>
{{OAuthPageScript.Js}}
</script>
{{turnstileScript}}
</body>
</html>
""";
    }
}
