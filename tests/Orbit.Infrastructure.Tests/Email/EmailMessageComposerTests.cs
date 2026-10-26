using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Orbit.Api.Branding;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Tests.Email;

public sealed class EmailMessageComposerTests
{
    private readonly EmailMessageComposer _composer = new("https://app.useorbit.org");

    [Theory]
    [InlineData("en", "123456 is your Orbit sign-in code", "It works for the next 5 minutes.")]
    [InlineData("pt-BR", "123456 é seu código de acesso ao Orbit", "Ele vale pelos próximos 5 minutos.")]
    public void VerificationCodeRendersLocalizedSubjectAndBothParts(string language, string subject, string preheader)
    {
        var message = _composer.ComposeVerificationCode("a+b@example.com", "123456", language);

        message.Subject.Should().Be(subject);
        message.Html.Should().Contain(preheader).And.Contain("pwa-512x512.png")
            .And.Contain("email=a%2Bb%40example.com&code=123456").And.NotContain("{{");
        message.Text.Should().Contain("123456");
    }

    [Theory]
    [InlineData("en", "123456", "Your sign-in code")]
    [InlineData("pt-BR", "123456", "Seu código de acesso")]
    [InlineData("en", "012345", "Your sign-in code")]
    [InlineData("pt-BR", "012345", "Seu código de acesso")]
    [InlineData("en", "000000", "Your sign-in code")]
    [InlineData("pt-BR", "000000", "Seu código de acesso")]
    public void VerificationCodeIsUnambiguousLiveText(string language, string code, string label)
    {
        var message = _composer.ComposeVerificationCode("a+b@example.com", code, language);

        using var scope = new AssertionScope();
        message.Subject.Should().StartWith(code + " ");
        message.Html.Should().MatchRegex($@"{Regex.Escape(label)}:</p>\s*<span\b[^>]*>{code}</span>");
        message.Text.Should().Contain($"{label}: {code}");

        var htmlBody = message.Html[message.Html.IndexOf("<body", StringComparison.Ordinal)..];
        var htmlText = WebUtility.HtmlDecode(Regex.Replace(htmlBody, "<[^>]+>", " "));
        foreach (var part in new[] { message.Subject, htmlText, message.Text ?? string.Empty })
        {
            Regex.Matches(part, @"\d+").Select(match => match.Value)
                .Where(value => value.Length == code.Length).Distinct()
                .Should().Equal(code);
        }
    }

    [Theory]
    [InlineData("en", "Welcome to Orbit")]
    [InlineData("pt-BR", "Boas-vindas ao Orbit")]
    public void WelcomeEncodesHtmlAndKeepsTextReadable(string language, string subject)
    {
        var message = _composer.ComposeWelcome("Ana & Co", language);

        message.Subject.Should().Be(subject);
        message.Html.Should().Contain("Ana &amp; Co").And.NotContain("#22094F");
        message.Text.Should().Contain("Ana & Co");
    }

    [Fact]
    public void WelcomeDoesNotSubstituteTokensInsideUserName()
    {
        _composer.ComposeWelcome("{{footer}}", "en").Html.Should().Contain("{{footer}}");
    }

    [Theory]
    [InlineData("en", "Confirm that you want to delete your Orbit account", "Confirm your new Orbit API key")]
    [InlineData("pt-BR", "Confirme a exclusão", "chave de API do Orbit")]
    public void SecurityCodesKeepTheirDistinctCopy(string language, string deletionSubject, string apiKeySubject)
    {
        var deletion = _composer.ComposeAccountDeletionCode("654321", language);
        var apiKey = _composer.ComposeApiKeyCreationCode("654321", language);

        deletion.Subject.Should().Contain(deletionSubject);
        apiKey.Subject.Should().Contain(apiKeySubject);
        deletion.Html.Should().Contain("654321");
        apiKey.Text.Should().Contain("654321");
        apiKey.Html.Should().NotContain("account deletion");
    }

    [Theory]
    [InlineData("en", "Confirm your place on the Orbit iOS list")]
    [InlineData("pt-BR", "Confirme sua vaga na lista do Orbit para iOS")]
    public void WaitlistIncludesConfirmationUrlAndText(string language, string subject)
    {
        const string url = "https://api.useorbit.org/api/waitlist/confirm?token=abc.def";
        var message = _composer.ComposeWaitlistConfirmation(url, language);

        message.Subject.Should().Contain(subject);
        message.Html.Should().Contain(url).And.NotContain("{{");
        message.Text.Should().Contain(url);
    }

    [Fact]
    public void SupportEncodesUserContentAndKeepsReplyText()
    {
        var message = _composer.ComposeSupport("<b>John</b>", "john@test.com", "Bug", "line1\nline2");

        message.Subject.Should().Be("[Orbit Support] Bug");
        message.Html.Should().Contain("&lt;b&gt;John&lt;/b&gt;").And.Contain("line1<br>line2")
            .And.Contain("Reply to this email to answer them.");
        message.Text.Should().Contain("<b>John</b>").And.Contain("line1\nline2");
    }

    [Theory]
    [InlineData("en", "Unsubscribe")]
    [InlineData("pt-BR", "Cancelar inscrição")]
    public void MarketingRendersLocalizedFooterAndEscapesUnsubscribeUrl(string language, string label)
    {
        var message = _composer.ComposeMarketing("News", "<p>Update</p>", language, "https://useorbit.org/u?a=1&b=2");

        message.Subject.Should().Be("News");
        message.Html.Should().Contain("<p>Update</p>").And.Contain(label)
            .And.Contain("https://useorbit.org/u?a=1&amp;b=2");
        message.Text.Should().BeNull();
    }

    [Theory]
    [InlineData("https://app.useorbit.org")]
    [InlineData("https://app-staging.useorbit.org")]
    public void EveryEmailUsesTheHostedFilledBrandExport(string frontendBaseUrl)
    {
        var composer = new EmailMessageComposer(frontendBaseUrl);
        foreach (var message in AllMessages(composer))
        {
            message.Html.Should().Contain($"src=\"{frontendBaseUrl}/pwa-512x512.png\"")
                .And.NotContain("logo-no-bg.png");
        }

        var asset = BrandAssets.TryRead("/orbit-icon-512.png");
        asset.Should().NotBeNull();
        Convert.ToHexStringLower(SHA256.HashData(asset!.Value.Content)).Should()
            .Be("7e581e38545f8b1d9bdeb7a78bbf7a066c0e5c7ac05b72ad0dab85056a06ab54");
    }

    [Theory]
    [InlineData(600, false)]
    [InlineData(600, true)]
    [InlineData(360, false)]
    [InlineData(360, true)]
    [InlineData(320, false)]
    [InlineData(320, true)]
    public void EveryEmailHeaderFitsTheCanvasAndKeepsTheMarkVisible(int viewportWidth, bool dark)
    {
        foreach (var message in AllMessages(_composer))
        {
            var header = Regex.Match(message.Html,
                "<td align=\"center\" style=\"padding: (?<top>\\d+)px (?<side>\\d+)px (?<bottom>\\d+)px;\">\\s*" +
                "<img src=\"[^\"]+\" width=\"(?<width>\\d+)\" height=\"(?<height>\\d+)\"[^>]+>");
            header.Success.Should().BeTrue();
            int Dimension(string name) => int.Parse(header.Groups[name].Value, CultureInfo.InvariantCulture);

            (Dimension("top") + Dimension("height") + Dimension("bottom")).Should()
                .BeLessThanOrEqualTo(80, "the content card must begin within the header rhythm")
                .And.Be(72);
            (Dimension("width") + 2 * Dimension("side")).Should().BeLessThanOrEqualTo(viewportWidth);
            Dimension("width").Should().Be(48);
            Dimension("height").Should().Be(48);
            header.Value.Should().Contain("display: block").And.Contain("border-radius: 50%");

            var canvas = dark
                ? Regex.Match(message.Html, @"body, \.orbit-canvas \{ background-color: (?<color>#[0-9A-Fa-f]{6})")
                : Regex.Match(message.Html, "<body[^>]+background-color: (?<color>#[0-9A-Fa-f]{6})");
            canvas.Success.Should().BeTrue();
            var background = canvas.Groups["color"].Value;
            var iconCanvas = "#09090B";
            var iconInk = "#F4F4F6";
            Contrast(iconInk, iconCanvas).Should().BeGreaterThanOrEqualTo(3);
            Math.Max(Contrast(iconCanvas, background), Contrast(iconInk, background)).Should()
                .BeGreaterThanOrEqualTo(3, "the filled disc or its ink must remain visible against the email canvas");
        }
    }

    private static EmailMessage[] AllMessages(EmailMessageComposer composer) =>
    [
        composer.ComposeVerificationCode("user@example.com", "123456", "pt-BR"),
        composer.ComposeWelcome("Ana", "en"),
        composer.ComposeAccountDeletionCode("123456", "en"),
        composer.ComposeApiKeyCreationCode("123456", "en"),
        composer.ComposeWaitlistConfirmation("https://example.com/confirm", "en"),
        composer.ComposeSupport("Ana", "user@example.com", "Support", "Message"),
        composer.ComposeMarketing("News", "<p>Update</p>", "en", "https://example.com/unsubscribe"),
    ];

    private static double Contrast(string first, string second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
            (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double Luminance(string color)
    {
        double Channel(int offset)
        {
            var value = int.Parse(color.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
