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
    private readonly OAuthAuthorizationStore _authStore =
        new(NullLogger<OAuthAuthorizationStore>.Instance, TimeProvider.System);
    private readonly OAuthController _controller;

    public OAuthLoginPageTests()
    {
        var googleSettings = Options.Create(new GoogleSettings
        {
            ClientId = "test-google-client-id",
            AllowedRedirectUris = ["https://api.useorbit.org/oauth/google/callback"]
        });
        _controller = new OAuthController(
            Substitute.For<IMediator>(), _authStore,
            Substitute.For<IGenericRepository<ApiKey>>(),
            Substitute.For<IUnitOfWork>(),
            googleSettings, new ConfigurationBuilder().Build(),
            Substitute.For<ILogger<OAuthController>>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.useorbit.org");
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        _authStore.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string RenderPage(string language = "en") => OAuthLoginPage.Render(
        "client-123", "https://claude.ai/callback", "client-state",
        "mcp-challenge", "S256", "mcp-nonce", error: null, language: language);

    private string RenderThroughController(string acceptLanguage)
    {
        _controller.HttpContext.Request.Headers.AcceptLanguage = acceptLanguage;
        return _controller.Authorize("client-123", "https://claude.ai/callback", "code",
            "client-state", "mcp-challenge", "S256")
            .Should().BeOfType<ContentResult>().Subject.Content!;
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
    public void Authorize_RendersEnglishForAnyRequestThatDoesNotAskForPortuguese(string acceptLanguage)
    {
        var page = RenderThroughController(acceptLanguage);

        page.Should().Contain("<html lang=\"en\">");
        page.Should().Contain("Sign in with your email");
        page.Should().Contain("Continue with Google");
        page.Should().Contain("In your account, Claude can:");
    }

    [Theory]
    [InlineData("pt-BR,pt;q=0.9")]
    [InlineData("pt")]
    public void Authorize_RendersBrazilianPortugueseForAPortugueseRequest(string acceptLanguage)
    {
        var page = RenderThroughController(acceptLanguage);

        page.Should().Contain("<html lang=\"pt-BR\">");
        page.Should().Contain("Entre com o seu email");
        page.Should().Contain("Digite o código");
        page.Should().Contain("Continuar com o Google");
        page.Should().Contain("Na sua conta, o Claude pode:");
        page.Should().Contain("Gerenciar seus hábitos, metas e tags");
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
