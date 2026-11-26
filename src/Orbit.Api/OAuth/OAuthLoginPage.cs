namespace Orbit.Api.OAuth;

/// <summary>
/// The MCP authorize page. It renders both steps of the email path plus the Google path, in the language the
/// request asked for, against the design system's own tokens.
/// </summary>
public static class OAuthLoginPage
{
    private const string AppleLogo = """<svg width="31" height="44" viewBox="0 0 31 44" aria-hidden="true" focusable="false"><path d="M15.7099491,14.8846154 C16.5675461,14.8846154 17.642562,14.3048315 18.28274,13.5317864 C18.8625238,12.8312142 19.2852829,11.852829 19.2852829,10.8744437 C19.2852829,10.7415766 19.2732041,10.6087095 19.2490464,10.5 C18.2948188,10.5362365 17.1473299,11.140178 16.4588366,11.9494596 C15.9152893,12.56548 15.4200572,13.5317864 15.4200572,14.5222505 C15.4200572,14.6671964 15.4442149,14.8121424 15.4562937,14.8604577 C15.5166879,14.8725366 15.6133185,14.8846154 15.7099491,14.8846154 Z M12.6902416,29.5 C13.8618881,29.5 14.3812778,28.714876 15.8428163,28.714876 C17.3285124,28.714876 17.6546408,29.4758423 18.9591545,29.4758423 C20.2395105,29.4758423 21.0971074,28.292117 21.9063891,27.1325493 C22.8123013,25.8038779 23.1867451,24.4993643 23.2109027,24.4389701 C23.1263509,24.4148125 20.6743484,23.4122695 20.6743484,20.5979021 C20.6743484,18.1579784 22.6069612,17.0588048 22.7156707,16.974253 C21.4353147,15.1382708 19.490623,15.0899555 18.9591545,15.0899555 C17.5217737,15.0899555 16.3501271,15.9596313 15.6133185,15.9596313 C14.8161157,15.9596313 13.7652575,15.1382708 12.521138,15.1382708 C10.1536872,15.1382708 7.75,17.0950413 7.75,20.7911634 C7.75,23.0861411 8.64383344,25.513986 9.74300699,27.0842339 C10.6851558,28.4129053 11.5065162,29.5 12.6902416,29.5 Z" fill="currentColor" fill-rule="nonzero"/></svg>""";
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
            ? $"<a class=\"btn btn-provider btn-apple\" id=\"apple\" href=\"{appleStartUrl}\">{AppleLogo}{appleLabel}</a>" : "";

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
<div class="providers">
<a class="btn btn-ghost btn-provider btn-google" id="google" href="{{googleStartUrl}}"><img src="/oauth/assets/google-mark.svg" width="20" height="20" alt="" aria-hidden="true">{{copy.GoogleLabel}}</a>
{{appleButton}}
</div>
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
