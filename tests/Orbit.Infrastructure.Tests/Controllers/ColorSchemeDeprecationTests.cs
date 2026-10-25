using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Application.Profile.Commands;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ColorSchemeDeprecationTests
{
    [Fact]
    public void RetiringInputs_NameTheOwningTicket()
    {
        const string ticket = "thomasluizon/orbit-tickets#685";
        MemberInfo[] members =
        [
            typeof(ProfileController).GetMethod(nameof(ProfileController.SetColorScheme))!,
            typeof(ProfileController.SetColorSchemeRequest),
            typeof(ProfileController.ApplyOnboardingRequest).GetProperty("ColorScheme")!,
            typeof(ApplyOnboardingCommand).GetProperty("ColorScheme")!
        ];
        foreach (var member in members)
        {
            var attribute = member.GetCustomAttribute<ObsoleteAttribute>();
            attribute.Should().NotBeNull();
            attribute!.Message.Should().Contain(ticket);
        }
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void GeneratedOpenApi_DeprecatesColorSchemeInputsAndPreservesCompatibility(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        paths.GetProperty("/api/Profile/color-scheme").GetProperty("put")
            .GetProperty("deprecated").GetBoolean().Should().BeTrue();
        schemas.GetProperty("SetColorSchemeRequest").GetProperty("deprecated").GetBoolean().Should().BeTrue();
        var onboarding = schemas.GetProperty("ApplyOnboardingRequest").GetProperty("properties");
        onboarding.GetProperty("colorScheme").GetProperty("deprecated").GetBoolean().Should().BeTrue();
        onboarding.GetProperty("weekStartDay").TryGetProperty("deprecated", out _).Should().BeFalse();
        paths.GetProperty("/api/Profile/onboarding/apply").GetProperty("post")
            .TryGetProperty("deprecated", out _).Should().BeFalse();
        paths.GetProperty("/api/Profile").GetProperty("get")
            .TryGetProperty("deprecated", out _).Should().BeFalse();

        var profile = schemas.GetProperty("ProfileResponse");
        profile.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(["aiMemoryEnabled", "adRewardsClaimedToday", "colorScheme"]);
        profile.GetProperty("properties").TryGetProperty("publicProfile", out _).Should().BeTrue();
    }
}
