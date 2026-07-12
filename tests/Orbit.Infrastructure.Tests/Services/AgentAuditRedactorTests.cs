using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AgentAuditRedactorTests
{
    [Theory]
    [InlineData("code")]
    [InlineData("code_verifier")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("password")]
    [InlineData("client_secret")]
    [InlineData("apiKey")]
    [InlineData("Authorization")]
    public void Redact_MasksSensitiveTopLevelFields(string key)
    {
        var json = $$"""{"{{key}}":"s3cret-value","email":"a@b.com"}""";

        var result = AgentAuditRedactor.Redact(json);

        result.Should().NotContain("s3cret-value");
        result.Should().Contain("***REDACTED***");
        result.Should().Contain("a@b.com");
    }

    [Fact]
    public void Redact_MasksNestedAndArrayFields()
    {
        var json = """{"outer":{"token":"TOK123"},"items":[{"code":"OTP789"}]}""";

        var result = AgentAuditRedactor.Redact(json);

        result.Should().NotContain("TOK123").And.NotContain("OTP789");
        result.Should().Contain("***REDACTED***");
    }

    [Fact]
    public void Redact_KeepsNonSensitiveFields()
    {
        var json = """{"email":"a@b.com","habitId":"h-123","count":5}""";

        var result = AgentAuditRedactor.Redact(json);

        result.Should().Contain("a@b.com").And.Contain("h-123").And.Contain("5");
        result.Should().NotContain("***REDACTED***");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("Name")]
    [InlineData("firstName")]
    [InlineData("last_name")]
    [InlineData("fullName")]
    [InlineData("displayName")]
    [InlineData("userName")]
    [InlineData("nickname")]
    public void Redact_MasksUserNameFields(string key)
    {
        var json = $$"""{"{{key}}":"Alice Johnson","email":"a@b.com"}""";

        var result = AgentAuditRedactor.Redact(json);

        result.Should().NotContain("Alice Johnson");
        result.Should().Contain("***REDACTED***");
        result.Should().Contain("a@b.com");
    }

    [Theory]
    [InlineData("bodyHtmlEn")]
    [InlineData("bodyHtmlPt")]
    [InlineData("BodyHtmlEn")]
    [InlineData("body_html_pt")]
    public void Redact_MasksMarketingBroadcastBody(string key)
    {
        var json = $$"""{"{{key}}":"<h1>Launch promo</h1>","subjectEn":"Big news"}""";

        var result = AgentAuditRedactor.Redact(json);

        result.Should().NotContain("Launch promo");
        result.Should().Contain("***REDACTED***");
        result.Should().Contain("Big news");
    }

    [Fact]
    public void Redact_MarketingBroadcast_MasksBothLocaleBodiesKeepsSubjectAndEmail()
    {
        var json = """
            {"subjectEn":"Weekly digest","subjectPt":"Resumo semanal",
             "bodyHtmlEn":"<p>Private copy</p>","bodyHtmlPt":"<p>Copia privada</p>",
             "testEmail":"admin@orbit.app"}
            """;

        var result = AgentAuditRedactor.Redact(json);

        result.Should().NotContain("Private copy").And.NotContain("Copia privada");
        result.Should().Contain("Weekly digest").And.Contain("Resumo semanal");
        result.Should().Contain("admin@orbit.app");
    }

    [Fact]
    public void Redact_MasksWholeBodyWhenNotJson()
    {
        AgentAuditRedactor.Redact("code=123456&grant_type=authorization_code")
            .Should().Be("***REDACTED***");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_ReturnsNullOrEmptyUnchanged(string? input)
    {
        AgentAuditRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_TruncatesLongPayloadToCap()
    {
        var json = $$"""{"note":"{{new string('x', 5000)}}"}""";

        var result = AgentAuditRedactor.Redact(json);

        result!.Length.Should().BeLessThanOrEqualTo(1000);
    }
}
