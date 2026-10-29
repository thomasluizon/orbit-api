using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Application.Profile.Commands;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ColorSchemeDeprecationTests
{
    [Fact]
    public void RetiredWrite_HasNoControllerRequestCommandOrDomainMutator()
    {
        typeof(ProfileController).GetMethod("SetColorScheme").Should().BeNull();
        typeof(ProfileController).GetNestedType("SetColorSchemeRequest").Should().BeNull();
        typeof(ApplyOnboardingCommand).Assembly.GetType(
            "Orbit.Application.Profile.Commands.SetColorSchemeCommand").Should().BeNull();
        typeof(ApplyOnboardingCommand).Assembly.GetType(
            "Orbit.Application.Profile.Commands.SetColorSchemeCommandHandler").Should().BeNull();
        typeof(Orbit.Domain.Entities.User).GetMethod("SetColorScheme").Should().BeNull();
        typeof(ProfileController.ApplyOnboardingRequest).GetProperty("ColorScheme").Should().BeNull();
        typeof(ApplyOnboardingCommand).GetProperty("ColorScheme").Should().BeNull();
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void GeneratedOpenApi_RemovesColorSchemeInputsAndPreservesReadContracts(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        paths.TryGetProperty("/api/Profile/color-scheme", out _).Should().BeFalse();
        schemas.TryGetProperty("SetColorSchemeRequest", out _).Should().BeFalse();
        var onboarding = schemas.GetProperty("ApplyOnboardingRequest").GetProperty("properties");
        onboarding.TryGetProperty("colorScheme", out _).Should().BeFalse();
        onboarding.GetProperty("weekStartDay").TryGetProperty("deprecated", out _).Should().BeFalse();
        paths.GetProperty("/api/Profile/onboarding/apply").GetProperty("post")
            .TryGetProperty("deprecated", out _).Should().BeFalse();
        paths.GetProperty("/api/Profile").GetProperty("get")
            .TryGetProperty("deprecated", out _).Should().BeFalse();

        var profile = schemas.GetProperty("ProfileResponse");
        profile.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(["aiMemoryEnabled", "adRewardsClaimedToday", "colorScheme", "handle"]);
        profile.GetProperty("properties").TryGetProperty("publicProfile", out _).Should().BeTrue();
    }
}
