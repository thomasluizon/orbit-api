using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Configuration;

public class BotProtectionTerraformTests
{
    [Theory]
    [InlineData("production", "production", true, false)]
    [InlineData("staging", "staging", true, false)]
    [InlineData("production", "staging", false, false)]
    [InlineData("staging", "production", false, false)]
    [InlineData("production", "production", true, true)]
    [InlineData("staging", "staging", true, true)]
    [InlineData("production", "staging", false, true)]
    [InlineData("staging", "production", false, true)]
    public async Task ProvisionedPolicy_AdmitsWidgetHostOnlyInItsEnvironment(
        string apiEnvironment, string widgetEnvironment, bool accepted, bool landing)
    {
        var root = RepositoryRoot();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "tests/Orbit.Infrastructure.Tests/Fixtures/BotProtection/TerraformHostnames.json")));
        foreach (var source in fixture.RootElement.GetProperty("sourceHashes").EnumerateObject())
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, source.Name))))
                .Should().Be(source.Value.GetString(), "regenerate the policy with Fixtures/BotProtection/generate.py after changing Terraform");

        var configuration = File.ReadAllText(Path.Combine(root, "infra/configuration.tf"));
        var environmentBlock = Regex.Match(configuration,
            $@"(?ms)^  {widgetEnvironment}_api_values\s*=.*?^  \}}\)?");
        environmentBlock.Success.Should().BeTrue();
        var callback = Regex.Match(environmentBlock.Value,
            "Google__AllowedRedirectUris__\\d+\\s*=\\s*\"(https://api[^\"]+/oauth/google/callback)\"");
        callback.Success.Should().BeTrue("the OAuth login page and callback are hosted by the API");
        var hostname = landing
            ? widgetEnvironment == "production" ? "useorbit.org" : "orbit-landing-staging.onrender.com"
            : new Uri(callback.Groups[1].Value).Host;
        var hostnames = fixture.RootElement.GetProperty("environments").GetProperty(apiEnvironment)
            .EnumerateObject().Select(value => value.Value.GetString()!).ToArray();
        var verifier = Substitute.For<ITurnstileVerificationService>();
        verifier.VerifyAsync("oauth-token", Arg.Any<CancellationToken>())
            .Returns(new TurnstileVerificationResult(true, [], hostname));
        var filter = new RequireBotProtectionFilter(Options.Create(new BotProtectionSettings
        {
            Enabled = true,
            AllowedHostnames = hostnames
        }), verifier, NullLogger<RequireBotProtectionFilter>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = landing ? "/api/waitlist/join" : "/oauth/send-code";
        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ControllerActionDescriptor()), [],
            new Dictionary<string, object?>
            {
                ["request"] = landing
                    ? new WaitlistController.JoinWaitlistRequest("person@example.com", TurnstileToken: "oauth-token")
                    : new OAuthController.SendCodeRequest("person@example.com", TurnstileToken: "oauth-token")
            }, new object());
        var ran = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, context.Filters, context.Controller));
        });

        ran.Should().Be(accepted, $"{apiEnvironment} must admit only its own widget hosting origins ({hostname})");
        if (accepted)
            context.Result.Should().BeNull();
        else
            context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void ProvisionedSwitch_StaysDisabledUntilClientAndReleaseReadiness()
    {
        var configuration = File.ReadAllText(Path.Combine(RepositoryRoot(), "infra/configuration.tf"));
        var values = Regex.Matches(configuration, "(?m)^\\s*BotProtection__Enabled\\s*=\\s*\"(true|false)\"")
            .Select(match => match.Groups[1].Value);
        values.Should().Equal("false", "false");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root is missing.");
    }
}
