using FluentAssertions;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Tests.Email;

public class EmailTemplateRendererTests
{
    private static EmailLayout Layout(bool gradient = false) => new(
        Lang: "en",
        Preheader: "Preview line",
        Footer: "The Orbit Team",
        LogoUrl: "https://app.useorbit.org/logo-no-bg.png",
        GradientHeader: gradient);

    private static Dictionary<string, string> VerificationCodeTokens() => new()
    {
        ["heading"] = "Your verification code",
        ["intro"] = "Use the code below to sign in to Orbit. It expires in 5 minutes.",
        ["code"] = "123456",
        ["cta"] = "Sign in to Orbit",
        ["signInUrl"] = "https://app.useorbit.org/login?email=user%40test.com&code=123456",
        ["warning"] = "If you didn't request this code, you can safely ignore this email.",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> WelcomeTokens() => new()
    {
        ["heading"] = "Welcome aboard, Thomas!",
        ["intro"] = "We're excited to have you on Orbit.",
        ["featuresTitle"] = "Here's what you can do:",
        ["feature1"] = "Create daily, weekly, or custom habits",
        ["feature2"] = "Track streaks and view your progress",
        ["feature3"] = "Get AI-powered insights on your routines",
        ["cta"] = "Get Started",
        ["ctaUrl"] = "https://app.useorbit.org",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> AccountDeletionTokens() => new()
    {
        ["heading"] = "Account deletion",
        ["intro"] = "You requested to delete your Orbit account.",
        ["codeLabel"] = "Use the code below to confirm:",
        ["code"] = "654321",
        ["warning"] = "If you didn't request this, ignore this email.",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> SupportTokens() => new()
    {
        ["fromName"] = "John",
        ["fromEmail"] = "john@test.com",
        ["subject"] = "Bug Report",
        ["message"] = "Found a bug",
    };

    [Fact]
    public void RenderHtml_ReplacesAllTokens()
    {
        var html = EmailTemplateRenderer.RenderHtml("VerificationCode", Layout(), VerificationCodeTokens());

        html.Should().NotContain("{{");
        html.Should().Contain("123456");
        html.Should().Contain("Your verification code");
        html.Should().Contain("https://app.useorbit.org/login?email=user%40test.com&code=123456");
    }

    [Fact]
    public void RenderHtml_ComposesSharedLayout()
    {
        var html = EmailTemplateRenderer.RenderHtml("VerificationCode", Layout(), VerificationCodeTokens());

        html.Should().Contain("<html lang=\"en\">");
        html.Should().Contain("Preview line");
        html.Should().Contain("https://app.useorbit.org/logo-no-bg.png");
        html.Should().Contain("The Orbit Team");
        html.Should().Contain("role=\"presentation\"");
    }

    [Fact]
    public void RenderHtml_GradientHeader_UsesGradientWithSolidFallback()
    {
        var html = EmailTemplateRenderer.RenderHtml("Welcome", Layout(gradient: true), WelcomeTokens());

        html.Should().Contain("linear-gradient(180deg, #22094F 0%, #020618 100%)");
        html.Should().Contain("bgcolor=\"#22094F\"");
    }

    [Fact]
    public void RenderHtml_PlainHeader_UsesCanvasColor()
    {
        var html = EmailTemplateRenderer.RenderHtml("VerificationCode", Layout(), VerificationCodeTokens());

        html.Should().NotContain("linear-gradient");
        html.Should().Contain("bgcolor=\"#020618\"");
    }

    [Fact]
    public void RenderHtml_TokenValuesAreNotReScanned()
    {
        var tokens = VerificationCodeTokens();
        tokens["heading"] = "{{warning}}";

        var html = EmailTemplateRenderer.RenderHtml("VerificationCode", Layout(), tokens);

        html.Should().Contain("{{warning}}");
    }

    [Fact]
    public void RenderHtml_UnknownTemplateToken_Throws()
    {
        var tokens = VerificationCodeTokens();
        tokens.Remove("code");

        var act = () => EmailTemplateRenderer.RenderHtml("VerificationCode", Layout(), tokens);

        act.Should().Throw<InvalidOperationException>().WithMessage("*code*");
    }

    [Fact]
    public void RenderHtml_MissingTemplate_Throws()
    {
        var act = () => EmailTemplateRenderer.RenderHtml("Nonexistent", Layout(), VerificationCodeTokens());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Nonexistent*");
    }

    [Fact]
    public void RenderText_ReplacesTokens()
    {
        var text = EmailTemplateRenderer.RenderText("VerificationCode", VerificationCodeTokens());

        text.Should().NotContain("{{");
        text.Should().Contain("123456");
        text.Should().Contain("Sign in to Orbit: https://app.useorbit.org/login");
        text.Should().NotContain("<");
    }

    [Theory]
    [InlineData("VerificationCode")]
    [InlineData("Welcome")]
    [InlineData("AccountDeletion")]
    [InlineData("Support")]
    public void AllEmbeddedTemplates_LoadAndRenderWithoutLeftoverTokens(string emailName)
    {
        var tokens = emailName switch
        {
            "VerificationCode" => VerificationCodeTokens(),
            "Welcome" => WelcomeTokens(),
            "AccountDeletion" => AccountDeletionTokens(),
            _ => SupportTokens(),
        };

        var html = EmailTemplateRenderer.RenderHtml(emailName, Layout(), tokens);
        var text = EmailTemplateRenderer.RenderText(emailName, tokens);

        html.Should().NotContain("{{");
        text.Should().NotContain("{{");
    }
}
