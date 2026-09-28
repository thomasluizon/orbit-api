using FluentAssertions;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Tests.Email;

public sealed class EmailMessageComposerTests
{
    private readonly EmailMessageComposer _composer = new("https://app.useorbit.org");

    [Theory]
    [InlineData("en", "Your Orbit verification code", "It expires in 5 minutes.")]
    [InlineData("pt-BR", "Seu código de verificação do Orbit", "Ele expira em 5 minutos.")]
    public void VerificationCodeRendersLocalizedSubjectAndBothParts(string language, string subject, string preheader)
    {
        var message = _composer.ComposeVerificationCode("a+b@example.com", "123456", language);

        message.Subject.Should().Be(subject);
        message.Html.Should().Contain(preheader).And.Contain("logo-no-bg.png")
            .And.Contain("email=a%2Bb%40example.com&code=123456").And.NotContain("{{");
        message.Text.Should().Contain("123456");
    }

    [Theory]
    [InlineData("en", "Welcome to Orbit")]
    [InlineData("pt-BR", "Boas-vindas")]
    public void WelcomeEncodesHtmlAndKeepsTextReadable(string language, string subjectFragment)
    {
        var message = _composer.ComposeWelcome("Ana & Co", language);

        message.Subject.Should().Contain(subjectFragment);
        message.Html.Should().Contain("Ana &amp; Co").And.Contain("#22094F");
        message.Text.Should().Contain("Ana & Co");
    }

    [Fact]
    public void WelcomeDoesNotSubstituteTokensInsideUserName()
    {
        _composer.ComposeWelcome("{{footer}}", "en").Html.Should().Contain("{{footer}}");
    }

    [Theory]
    [InlineData("en", "Confirm your Orbit account deletion", "Confirm your Orbit API key creation")]
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
    [InlineData("en", "Confirm your spot on the Orbit iOS waitlist")]
    [InlineData("pt-BR", "Confirme sua vaga na lista de espera")]
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
            .And.Contain("Reply directly to respond to the user.");
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
}
