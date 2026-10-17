using FluentAssertions;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Tests.Email;

public class EmailTemplateRendererTests
{
    private static EmailLayout Layout() => new(
        Lang: "en",
        Preheader: "Preview line",
        Footer: "The Orbit Team",
        LogoUrl: "https://app.useorbit.org/logo-no-bg.png");

    private static Dictionary<string, string> VerificationCodeTokens() => new()
    {
        ["heading"] = "Your sign-in code",
        ["intro"] = "You asked to sign in to Orbit. Use the code below, or tap the button.",
        ["code"] = "123456",
        ["cta"] = "Sign in",
        ["signInUrl"] = "https://app.useorbit.org/login?email=user%40test.com&code=123456",
        ["warning"] = "If you did not ask for this code, ignore this email.",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> WelcomeTokens() => new()
    {
        ["heading"] = "You are in, Alex",
        ["intro"] = "Orbit keeps one routine in front of you.",
        ["featuresTitle"] = "Where to start",
        ["feature1"] = "Add one habit you want to keep.",
        ["feature2"] = "Tell Astra what you did, in your own words.",
        ["feature3"] = "Miss a day and Orbit shows you the way back in.",
        ["cta"] = "Add your first habit",
        ["ctaUrl"] = "https://app.useorbit.org",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> AccountDeletionTokens() => new()
    {
        ["heading"] = "Delete your Orbit account",
        ["intro"] = "You asked to delete your Orbit account. This cannot be undone.",
        ["codeLabel"] = "Enter this code to confirm:",
        ["code"] = "654321",
        ["warning"] = "If you did not ask for this, ignore this email.",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> ApiKeyCreationTokens() => new()
    {
        ["heading"] = "Create an API key",
        ["intro"] = "You asked to create an Orbit API key.",
        ["codeLabel"] = "Enter this code to confirm:",
        ["code"] = "654321",
        ["warning"] = "If you did not ask for this, ignore this email. No key is created.",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> WaitlistConfirmationTokens() => new()
    {
        ["heading"] = "Confirm your place",
        ["intro"] = "One step left.",
        ["cta"] = "Confirm my place",
        ["confirmUrl"] = "https://api.useorbit.org/api/waitlist/confirm?token=abc.def",
        ["warning"] = "If you did not join the Orbit list, you can ignore this email.",
        ["footer"] = "The Orbit Team",
    };

    private static Dictionary<string, string> SupportTokens() => new()
    {
        ["heading"] = "Support request",
        ["fromLabel"] = "From",
        ["subjectLabel"] = "Subject",
        ["fromName"] = "John",
        ["fromEmail"] = "john@test.com",
        ["subject"] = "Bug report",
        ["message"] = "Found a bug",
        ["footer"] = "Reply to this email to answer them.",
    };

    private static Dictionary<string, string> TokensFor(string emailName) => emailName switch
    {
        "VerificationCode" => VerificationCodeTokens(),
        "Welcome" => WelcomeTokens(),
        "AccountDeletion" => AccountDeletionTokens(),
        "ApiKeyCreation" => ApiKeyCreationTokens(),
        "WaitlistConfirmation" => WaitlistConfirmationTokens(),
        _ => SupportTokens(),
    };

    [Fact]
    public void RenderHtml_ReplacesAllTokens()
    {
        var html = EmailTemplateRenderer.RenderHtml("VerificationCode", Layout(), VerificationCodeTokens());

        html.Should().NotContain("{{");
        html.Should().Contain("123456");
        html.Should().Contain("Your sign-in code");
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
        text.Should().Contain("Sign in: https://app.useorbit.org/login");
        text.Should().NotContain("<");
    }

    [Theory]
    [InlineData("VerificationCode")]
    [InlineData("Welcome")]
    [InlineData("AccountDeletion")]
    [InlineData("ApiKeyCreation")]
    [InlineData("WaitlistConfirmation")]
    [InlineData("Support")]
    public void AllEmbeddedTemplates_LoadAndRenderWithoutLeftoverTokens(string emailName)
    {
        var tokens = TokensFor(emailName);

        var html = EmailTemplateRenderer.RenderHtml(emailName, Layout(), tokens);
        var text = EmailTemplateRenderer.RenderText(emailName, tokens);

        html.Should().NotContain("{{");
        text.Should().NotContain("{{");
    }

    [Theory]
    [InlineData("VerificationCode")]
    [InlineData("Welcome")]
    [InlineData("AccountDeletion")]
    [InlineData("ApiKeyCreation")]
    [InlineData("WaitlistConfirmation")]
    [InlineData("Support")]
    public void EveryTemplate_CarriesNoBannedDecoration(string emailName)
    {
        var html = EmailTemplateRenderer.RenderHtml(emailName, Layout(), TokensFor(emailName));

        html.Should().NotContain("linear-gradient");
        html.Should().NotContain("box-shadow");
        html.Should().NotContain("#7F46F7");
        html.Should().NotContain("#22094F");
    }

    [Theory]
    [InlineData("VerificationCode")]
    [InlineData("Welcome")]
    [InlineData("AccountDeletion")]
    [InlineData("ApiKeyCreation")]
    [InlineData("WaitlistConfirmation")]
    [InlineData("Support")]
    public void EveryTemplate_DeclaresBothColorSchemes(string emailName)
    {
        var html = EmailTemplateRenderer.RenderHtml(emailName, Layout(), TokensFor(emailName));

        html.Should().Contain("name=\"color-scheme\" content=\"light dark\"");
        html.Should().Contain("prefers-color-scheme: dark");
        html.Should().Contain("background-color: #FAFAFA");
    }

    [Theory]
    [InlineData("VerificationCode")]
    [InlineData("Welcome")]
    [InlineData("WaitlistConfirmation")]
    public void EveryCallToAction_CarriesTheGrantedAccentFill(string emailName)
    {
        var html = EmailTemplateRenderer.RenderHtml(emailName, Layout(), TokensFor(emailName));

        html.Should().Contain("background-color: #C4530F");
        html.Should().Contain("border-radius: 999px");
    }

    [Fact]
    public void RenderLayout_InsertsMarketingBodyVerbatim()
    {
        var html = EmailTemplateRenderer.RenderLayout(Layout(), "<p>{{notAToken}}</p>");

        html.Should().Contain("<p>{{notAToken}}</p>");
    }
}
