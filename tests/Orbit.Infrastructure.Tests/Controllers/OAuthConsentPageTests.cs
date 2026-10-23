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
            .And.Contain("class=\"action-row\"")
            .And.Contain("class=\"btn btn-sm btn-primary\" name=\"access\" value=\"read_only\"")
            .And.Contain("class=\"btn btn-sm btn-ghost\" name=\"access\" value=\"requested\"");
        page.IndexOf("value=\"requested\"", StringComparison.Ordinal).Should()
            .BeLessThan(page.IndexOf("value=\"read_only\"", StringComparison.Ordinal));
        var groups = Regex.Match(page, "<ul id=\"permission-groups\">(.*?)</ul>", RegexOptions.Singleline);
        var labels = Regex.Matches(groups.Groups[1].Value, "<li>(.*?)</li>")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        labels.Should().HaveCount(8).And.OnlyHaveUniqueItems();
        OAuthPageStyles.Css.Should().Contain(".action-row {").And.Contain("gap: var(--s-3);")
            .And.Contain("justify-content: flex-end;").And.Contain(".action-row .btn { width: auto; }")
            .And.Contain(".action-row .btn { width: 100%; }");
    }

    [Fact]
    public void Render_EncodesTheTicket_AndKeepsRawScopesInert()
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
