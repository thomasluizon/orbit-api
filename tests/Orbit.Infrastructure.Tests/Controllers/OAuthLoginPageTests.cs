using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OAuthLoginPageTests : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _oauthDb = new();
    private readonly OAuthAuthorizationStore _authStore;
    private readonly OAuthController _controller;
    private readonly IConfigurationRoot _configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

    public OAuthLoginPageTests()
    {
        _authStore = new OAuthAuthorizationStore(new OAuthStateStore(_oauthDb.Context, TimeProvider.System), TimeProvider.System);
        var googleSettings = Options.Create(new GoogleSettings
        {
            ClientId = "test-google-client-id",
            AllowedRedirectUris = ["https://api.useorbit.org/oauth/google/callback"]
        });
        _controller = new OAuthController(
            Substitute.For<IMediator>(), _authStore,
            Substitute.For<IGenericRepository<ApiKey>>(),
            Substitute.For<IUnitOfWork>(),
            googleSettings, _configuration,
            Substitute.For<ILogger<OAuthController>>(), Substitute.For<IOAuthClientMetadataService>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.useorbit.org");
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        _oauthDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string RenderPage(string language = "en") => OAuthLoginPage.Render(
        "client-123", "https://claude.ai/callback", "client-state",
        "mcp-challenge", "S256", "mcp-nonce", error: null, language: language);

    private async Task<string> RenderThroughController(string acceptLanguage)
    {
        _controller.HttpContext.Request.Headers.AcceptLanguage = acceptLanguage;
        return (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "client-state",
            CodeChallenge = "mcp-challenge",
            CodeChallengeMethod = "S256",
        }))
            .Should().BeOfType<ContentResult>().Subject.Content!;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task EveryPage_LinksBrandIconsAndUsesTheSharedShell(string language)
    {
        _controller.Request.Headers.AcceptLanguage = language;
        var consentTicket = _authStore.CreatePendingConsent(new ConsentEntry(Guid.NewGuid(), "challenge",
            "https://claude.ai/callback", "client", "state", null, language));
        var consent = _controller.Consent(consentTicket).Should().BeOfType<ContentResult>().Subject;
        var invalid = (await _controller.GoogleCallback("unknown", null, null, CancellationToken.None))
            .Should().BeOfType<ContentResult>().Subject;
        invalid.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        invalid.ContentType.Should().Be("text/html");
        var invalidStart = (await _controller.GoogleStart("client", "https://claude.ai/callback", "", "challenge", "S256"))
            .Should().BeOfType<ContentResult>().Subject;
        var invalidConsent = _controller.Consent("missing").Should().BeOfType<ContentResult>().Subject;
        var invalidGrant = _controller.GrantConsent("missing", "requested").Should().BeOfType<ContentResult>().Subject;
        foreach (var errorPage in new[] { invalidStart, invalidConsent, invalidGrant })
            errorPage.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        foreach (var page in new[] { RenderPage(language), consent.Content!, invalid.Content!,
                     invalidStart.Content!, invalidConsent.Content!, invalidGrant.Content! })
        {
            page.Should().Contain($"<html lang=\"{language}\">");
            page.Should().Contain("<link rel=\"icon\" href=\"/favicon.ico\" sizes=\"16x16 32x32 48x48\">");
            page.Should().Contain("<link rel=\"icon\" href=\"/favicon.svg\" type=\"image/svg+xml\" sizes=\"any\">");
            page.Should().Contain("<link rel=\"apple-touch-icon\" href=\"/apple-touch-icon.png\" sizes=\"180x180\">");
            page.Should().Contain("<main class=\"card\">");
            page.Should().Contain(OAuthPageStyles.Css);
        }
        invalid.Content.Should().Contain(language == "pt-BR"
            ? "Esta solicitação de autorização é inválida ou expirou."
            : "This authorization request is invalid or expired.");
        invalid.Content.Should().Contain(language == "pt-BR"
            ? "Volte ao seu cliente MCP e tente novamente."
            : "Return to your MCP client and try again.");
        invalid.Content.Should().NotContain("<form").And.NotContain("<script");
    }

    [Fact]
    public void Render_AppleConfigured_PreservesMcpAuthorizationParameters()
    {
        var html = OAuthLoginPage.Render("client", "https://claude.ai/callback", "client-state", "challenge", "S256", appleSignInEnabled: true);
        html.Should().Contain("Sign in with Apple").And.Contain("/oauth/apple/start?").And.Contain("code_challenge=challenge");
        OAuthLoginPage.Render("client", "https://claude.ai/callback", "client-state", "challenge", "S256").Should().NotContain("id=\"apple\"");
    }

    [Fact]
    public void GoogleButton_UsesTheSharedDecorativeMarkAndBrandSpacing()
    {
        var page = RenderPage();
        page.Should().Contain("<img src=\"/oauth/assets/google-mark.svg\" width=\"20\" height=\"20\" alt=\"\" aria-hidden=\"true\">");
        page.Should().Contain(".btn-google { padding-inline: var(--s-3); gap: 10px; }");
    }

    [Fact]
    public void Page_DrawsTheOrbitalMarkWithTheAccentOnItsMoon()
    {
        var page = RenderPage();

        page.Should().Contain("data-orbit-mark=\"accent\"");
        page.Should().Contain("fill=\"var(--primary, currentColor)\"");
        page.Should().Contain("<title id=\"orbit-mark-title\">Orbit</title>");
        page.Should().NotContain("<div class=\"logo\">O</div>");
    }

    [Fact]
    public void Page_CarriesNoGradientAndNoThirdPartyFontHost()
    {
        var page = RenderPage();

        page.Should().NotContain("gradient");
        page.Should().NotContain("fonts.googleapis.com");
        page.Should().NotContain("fonts.gstatic.com");
        page.Should().NotContain("Manrope");
    }

    [Fact]
    public void Page_SelfHostsEveryFaceItDeclares()
    {
        var page = RenderPage();

        foreach (var name in OAuthPageAssets.Names)
            page.Should().Contain($"/oauth/assets/{name}");
        page.Should().Contain("'Geist Sans'");
        page.Should().Contain("'Space Grotesk'");
        page.Should().Contain("'Geist Mono'");
    }

    [Fact]
    public void Page_ReadsEveryColourFromTheTokenBlock()
    {
        var offenders = RenderPage()
            .Split('\n')
            .Where(line => Regex.IsMatch(line, "#[0-9A-Fa-f]{3,8}\\b") && !line.TrimStart().StartsWith("--"))
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Page_FollowsTheSystemColourSchemeAndCarriesNoEmoji()
    {
        var page = RenderPage();

        page.Should().Contain("@media (prefers-color-scheme: light)");
        page.Should().Contain("content=\"dark light\"");
        Regex.IsMatch(page, "[\\uD800-\\uDBFF]").Should().BeFalse();
    }

    [Fact]
    public void Page_DrawsSixCodeCellsBehindOneRealInput()
    {
        var page = RenderPage();

        Regex.Matches(page, "<span class=\"cell\" data-cell").Should().HaveCount(6);
        Regex.Matches(page, "id=\"code-input\"").Should().ContainSingle();
        page.Should().Contain("autocomplete=\"one-time-code\"");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void Page_ActiveCodeCellHasOneInsetIndicator_AndKeepsErrorAndDisabledStates(string language)
    {
        var css = RenderedCss(RenderPage(language));

        var active = CssRule(css, ".cell[data-active]");
        active.Should().Contain("box-shadow: inset 0 0 0 2px var(--primary);")
            .And.NotContain("outline");
        CssRule(css, ".code input").Should().Contain("outline: none;").And.Contain("opacity: 0;");
        CssRule(css, ".code[data-error] .cell").Should().Contain("box-shadow: inset 0 0 0 2px var(--status-bad);");
        css.IndexOf(".code[data-error] .cell", StringComparison.Ordinal)
            .Should().BeGreaterThan(css.IndexOf(".cell[data-active]", StringComparison.Ordinal));
        CssRule(css, ".code[data-disabled]").Should().Contain("opacity: 0.4;");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void EveryPage_UsesOneFocusIndicatorForEachControl(string language)
    {
        var login = OAuthLoginPage.Render("client", "https://claude.ai/callback", "state", "challenge", "S256",
            language: language, appleSignInEnabled: true);
        var ticket = _authStore.CreatePendingConsent(new ConsentEntry(Guid.NewGuid(), "challenge",
            "https://claude.ai/callback", "client", "state", null, language));
        var consent = _controller.Consent(ticket).Should().BeOfType<ContentResult>().Subject.Content!;
        _controller.Request.Headers.AcceptLanguage = language;
        var invalid = _controller.Consent("missing").Should().BeOfType<ContentResult>().Subject.Content!;
        var completed = OAuthConsentPage.RenderCompleted(language);

        foreach (var page in new[] { login, consent, invalid, completed })
        {
            var css = RenderedCss(page);
            CssRule(css, ":focus-visible").Should().Contain("outline: 2px solid var(--primary);")
                .And.Contain("outline-offset: 2px;").And.NotContain("box-shadow");
            CssRule(css, ".control:focus-within").Should().Contain("outline: 2px solid var(--primary);")
                .And.Contain("outline-offset: 2px;").And.NotContain("box-shadow");
            CssRule(css, ".control input").Should().Contain("outline: none;").And.NotContain("box-shadow");
            CssRule(css, ".code input").Should().Contain("outline: none;").And.NotContain("box-shadow");
            CssRule(css, ".cell[data-active]").Should().NotContain("outline");
            Regex.Matches(css, @"[^{}]*:focus(?:-visible|-within)?[^{}]*\{[^{}]*\}")
                .Should().HaveCount(2, "only the shared outline and the email wrapper own focus rules");
            CssRule(css, ".btn").Should().Contain("border-radius: var(--r-pill);");
            CssRule(css, ".control").Should().Contain("border-radius: var(--r-well);");
        }

        var loginControls = Regex.Matches(login, @"<(?:input|button|a)\b[^>]*>").Select(match => match.Value).ToArray();
        loginControls.Should().HaveCount(8);
        foreach (var id in new[] { "email", "code-input" })
            loginControls.Should().ContainSingle(tag => tag.Contains($"id=\"{id}\"", StringComparison.Ordinal)
                && tag.StartsWith("<input", StringComparison.Ordinal));
        foreach (var id in new[] { "send-code", "google", "apple", "change-email", "verify", "resend" })
            loginControls.Should().ContainSingle(tag => tag.Contains($"id=\"{id}\"", StringComparison.Ordinal)
                && tag.Contains("class=\"btn ", StringComparison.Ordinal));
        Regex.Matches(consent, @"<button\b[^>]*>").Should().HaveCount(3)
            .And.OnlyContain(match => match.Value.Contains("class=\"btn ", StringComparison.Ordinal));
        Regex.Matches(consent, @"<summary>").Should().ContainSingle();
        foreach (var terminalPage in new[] { invalid, completed })
            Regex.Matches(terminalPage, @"<(?:input|button|a|summary)\b").Should().BeEmpty();
    }

    [Fact]
    public void Page_ActiveCodeCellUsesABorderWhenForcedColorsRemoveShadows()
    {
        var css = RenderedCss(RenderPage());
        var forcedColors = Regex.Match(css, @"@media\s*\(forced-colors:\s*active\)\s*\{((?:[^{}]|\{[^{}]*\})*)\}");

        forcedColors.Success.Should().BeTrue("forced colors remove the inset shadow");
        CssRule(forcedColors.Groups[1].Value, ".cell").Should().Contain("border: 1px solid CanvasText;");
        CssRule(forcedColors.Groups[1].Value, ".cell[data-active]").Should()
            .Contain("border: 2px solid Highlight;").And.NotContain("outline");
    }

    private static string RenderedCss(string page)
    {
        var style = Regex.Match(page, "<style>(.*?)</style>", RegexOptions.Singleline);
        style.Success.Should().BeTrue();
        return style.Groups[1].Value;
    }

    private static string CssRule(string css, string selector)
    {
        var rule = Regex.Match(css, @"(?:^|[{}])\s*" + Regex.Escape(selector) + @"\s*\{([^{}]*)\}");
        rule.Success.Should().BeTrue($"the rendered stylesheet must define {selector}");
        return rule.Groups[1].Value;
    }

    [Fact]
    public void Page_KeepsBothSignInPathsAndSendsTheRequestLanguage()
    {
        var page = RenderPage();

        page.Should().Contain("/oauth/send-code");
        page.Should().Contain("/oauth/verify-code");
        page.Should().Contain("/oauth/google/start?client_id=");
        page.Should().Contain("language: request.language");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-GB,en;q=0.9")]
    [InlineData("")]
    public async Task Authorize_RendersEnglishForAnyRequestThatDoesNotAskForPortuguese(string acceptLanguage)
    {
        var page = (await RenderThroughController(acceptLanguage));

        page.Should().Contain("<html lang=\"en\">");
        page.Should().Contain("Sign in with your email");
        page.Should().Contain("Continue with Google");
        page.Should().Contain("In your account, Claude can:");
    }

    [Theory]
    [InlineData("pt-BR,pt;q=0.9")]
    [InlineData("pt")]
    public async Task Authorize_RendersBrazilianPortugueseForAPortugueseRequest(string acceptLanguage)
    {
        var page = (await RenderThroughController(acceptLanguage));

        page.Should().Contain("<html lang=\"pt-BR\">");
        page.Should().Contain("Entre com o seu email");
        page.Should().Contain("Digite o código");
        page.Should().Contain("Continuar com o Google");
        page.Should().Contain("Na sua conta, o Claude pode:");
        page.Should().Contain("Gerenciar seus hábitos, metas e tags");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Authorize_OnlyLoadsTurnstileWhenEnabled_AndNeverPublishesSecret(bool enabled)
    {
        _configuration["BotProtection:Enabled"] = enabled.ToString();
        _configuration["BotProtection:SiteKey"] = "public-site-key";
        _configuration["BotProtection:SecretKey"] = "private-secret-key";

        var page = (await RenderThroughController("pt-BR"));
        var requestJson = Regex.Match(page, "<script type=\"application/json\" id=\"oauth-request\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        using var request = JsonDocument.Parse(requestJson);

        request.RootElement.GetProperty("botProtectionEnabled").GetBoolean().Should().Be(enabled);
        if (enabled)
        {
            page.Should().Contain("<div id=\"turnstile-widget\"></div>");
            page.Should().Contain("src=\"https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit&amp;onload=onOAuthTurnstileLoad\"");
            request.RootElement.GetProperty("turnstileSiteKey").GetString().Should().Be("public-site-key");
        }
        else
        {
            page.Should().NotContain("<div id=\"turnstile-widget\"></div>");
            page.Should().NotContain("challenges.cloudflare.com");
            request.RootElement.GetProperty("turnstileSiteKey").ValueKind.Should().Be(JsonValueKind.Null);
        }
        page.Should().NotContain("private-secret-key");
    }

    [Fact]
    public void Page_EscapesSiteKeyInsideTheRequestJson()
    {
        var page = OAuthLoginPage.Render("client", "https://claude.ai/callback", "state", "challenge", "S256",
            botProtectionEnabled: true, turnstileSiteKey: "</script><script>alert(1)</script>");

        page.Should().NotContain("</script><script>alert(1)</script>");
        page.Should().Contain("\\u003C/script\\u003E");
    }

    [Fact]
    public void PageJson_KeepsAccentsReadableAndEscapesWhatCouldEndTheScriptBlock()
    {
        var written = OAuthPageJson.Write(new { line = "</script><b>código & 'aspas'" });

        written.Should().Contain("código");
        written.Should().NotContain("</script>");
        written.Should().NotContain("<b>");
        written.Should().Contain("\\u003C");
        JsonSerializer.Deserialize<Dictionary<string, string>>(written)!["line"]
            .Should().Be("</script><b>código & 'aspas'");
    }

    [Fact]
    public void Asset_ServesEverySelfHostedFontWithAnImmutableCache()
    {
        foreach (var name in OAuthPageAssets.Names)
        {
            var file = _controller.Asset(name).Should().BeOfType<FileContentResult>().Subject;

            file.ContentType.Should().Be("font/woff2");
            file.FileContents.Should().NotBeEmpty();
            _controller.Response.Headers.CacheControl.ToString()
                .Should().Be($"public, max-age={OAuthPageAssets.CacheSeconds}, immutable");
        }
    }

    [Theory]
    [InlineData("unknown.woff2")]
    [InlineData("../appsettings.json")]
    [InlineData("geist-latin.woff2.map")]
    public void Asset_RefusesAnyNameOutsideTheAllowlist(string name)
    {
        _controller.Asset(name).Should().BeOfType<NotFoundResult>();
    }
}
