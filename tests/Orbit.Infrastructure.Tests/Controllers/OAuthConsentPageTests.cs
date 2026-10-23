using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Orbit.Api.OAuth;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OAuthConsentPageTests
{
    [Theory]
    [InlineData("en", "Technical permissions")]
    [InlineData("pt-BR", "Permissões técnicas")]
    public void EveryCatalogScope_HasExactlyOneHumanGroup_AndOnlyAppearsInTheDisclosure(string language, string disclosure)
    {
        foreach (var scope in AgentScopes.All)
        {
            var page = Render(language, [scope]);
            var details = Regex.Match(page, "<details>.*?</details>", RegexOptions.Singleline);
            details.Success.Should().BeTrue(scope);
            WebUtility.HtmlDecode(details.Value).Should().Contain($"<summary>{disclosure}</summary>")
                .And.Contain($"<li><code>{scope}</code></li>");
            page.Replace(details.Value, "").Should().NotContain(scope);
            var groups = Regex.Match(page, "<ul id=\"permission-groups\">(.*?)</ul>", RegexOptions.Singleline);
            groups.Success.Should().BeTrue(scope);
            Regex.Matches(groups.Groups[1].Value, "<li>.+?</li>").Count.Should().Be(1, scope);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void DefaultPermissions_AreDeduplicated_AndUseTheSharedMarkAndActionRow(string language)
    {
        var page = Render(language);
        page.Should().Contain(OAuthPageStyles.Mark).And.Contain(OAuthPageStyles.Css)
            .And.Contain("class=\"action-row\"");
        var groups = Regex.Match(page, "<ul id=\"permission-groups\">(.*?)</ul>", RegexOptions.Singleline);
        var labels = Regex.Matches(groups.Groups[1].Value, "<li>(.*?)</li>")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        labels.Should().HaveCount(8).And.OnlyHaveUniqueItems();
        OAuthPageStyles.Css.Should().Contain(".action-row {").And.Contain("gap: var(--s-3);")
            .And.Contain("justify-content: flex-end;").And.Contain(".action-row .btn { width: auto; }")
            .And.Contain(".action-row .btn { width: 100%; }");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void ActionButtons_UseSmallSharedVariants_WithCancelAndRequestedBeforeReadOnly(string language)
    {
        var page = Render(language);
        var copy = OAuthPageStrings.For(language).Consent;
        var row = Regex.Match(page, "<div class=\"action-row\">(.*?)</div>", RegexOptions.Singleline);
        row.Success.Should().BeTrue();
        var buttons = Regex.Matches(row.Groups[1].Value, "<button\\b[^>]*>.*?</button>")
            .Select(match => WebUtility.HtmlDecode(match.Value)).ToArray();
        buttons.Should().HaveCount(3).And.OnlyContain(button => button.Contains("class=\"btn btn-sm ", StringComparison.Ordinal));
        buttons[0].Should().Contain("btn-ghost\"").And.Contain("formaction=\"/oauth/consent/cancel\"")
            .And.Contain($">{copy.Cancel}</button>").And.NotContain("name=\"access\"");
        buttons[1].Should().Contain("btn-ghost\"").And.Contain("name=\"access\" value=\"requested\"")
            .And.Contain($">{copy.Requested}</button>");
        buttons[2].Should().Contain("btn-primary\"").And.Contain("name=\"access\" value=\"read_only\"")
            .And.Contain($">{copy.ReadOnly}</button>");
        page.Should().NotContain("btn-quiet");
    }

    [Fact]
    public void Render_EncodesTheTicket()
    {
        var page = OAuthConsentPage.Render("\"><script>alert(1)</script>", Entry("en", [AgentScopes.ReadHabits]));
        page.Should().Contain("value=\"&quot;&gt;&lt;script&gt;alert(1)&lt;/script&gt;\"")
            .And.NotContain("<script>alert(1)</script>");
    }

    private static string Render(string language, IReadOnlyList<string>? scopes = null) =>
        OAuthConsentPage.Render("ticket", Entry(language, scopes));

    private static ConsentEntry Entry(string language, IReadOnlyList<string>? scopes) =>
        new(Guid.NewGuid(), "challenge", "https://claude.ai/callback", "client", "state", null, language, scopes);
}
