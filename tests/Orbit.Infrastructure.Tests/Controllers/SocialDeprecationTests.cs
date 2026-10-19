using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Controllers;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public class SocialDeprecationTests
{
    [Fact]
    public void RemovedControllers_HaveNoHttpActionsOrCatalogEntries()
    {
        var controllerNames = typeof(ChatController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .Select(type => type.Name);
        controllerNames.Should().NotContain("ChallengesController")
            .And.NotContain("AccountabilityController")
            .And.Contain("FriendsController")
            .And.Contain("PublicProfileController");

        var actions = new AgentCatalogService().GetCapabilities()
            .SelectMany(capability => capability.ControllerActionKeys ?? []);
        actions.Should().NotContain(action => action.StartsWith("ChallengesController.", StringComparison.Ordinal)
            || action.StartsWith("AccountabilityController.", StringComparison.Ordinal));
        actions.Should().Contain("FriendsController.SendCheer");
    }

    public static TheoryData<string, string, string> RetiringRoutes()
    {
        (string Path, string Method)[] routes =
        [
            ("/api/friends", "get"),
            ("/api/friends/{userId}/profile", "get"),
            ("/api/friends/invite-preview", "get"),
            ("/api/friends/feed", "get"),
            ("/api/friends/cheers", "get"),
            ("/api/friends/requests", "post"),
            ("/api/friends/requests/{friendshipId}/accept", "post"),
            ("/api/friends/{friendUserId}", "delete"),
            ("/api/friends/cheers", "post"),
            ("/api/friends/block", "post"),
            ("/api/friends/block/{blockedUserId}", "delete"),
            ("/api/friends/report", "post"),
            ("/api/u/{slug}", "get"),
            ("/api/Profile/handle", "put"),
            ("/api/Profile/social-opt-in", "put"),
            ("/api/Profile/public", "put")
        ];

        var cases = new TheoryData<string, string, string>();
        foreach (var file in new[] { "openapi.json", "openapi_v2.json" })
            foreach (var (path, method) in routes)
                cases.Add(file, path, method);
        return cases;
    }

    [Theory]
    [MemberData(nameof(RetiringRoutes))]
    public void GeneratedOpenApi_RetiringRouteRemainsAvailableAndDeprecated(string file, string path, string method)
    {
        using var document = ReadDocument(file);
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

        operation.TryGetProperty("deprecated", out var deprecated).Should().BeTrue();
        deprecated.GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void GeneratedOpenApi_RequiredSocialProfileFieldsRemainAvailableAndDeprecated(string file)
    {
        using var document = ReadDocument(file);
        var paths = document.RootElement.GetProperty("paths");
        paths.GetProperty("/api/Profile").GetProperty("get")
            .TryGetProperty("deprecated", out _).Should().BeFalse();

        var profile = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProfileResponse");
        var properties = profile.GetProperty("properties");
        foreach (var name in new[] { "handle", "socialOptIn" })
        {
            var property = properties.GetProperty(name);
            property.TryGetProperty("deprecated", out var deprecated).Should().BeTrue();
            deprecated.GetBoolean().Should().BeTrue();
        }

        properties.TryGetProperty("publicProfile", out _).Should().BeTrue();
        properties.GetProperty("uses24HourClock").TryGetProperty("deprecated", out _).Should().BeFalse();
        profile.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(["handle", "socialOptIn"]).And.NotContain("publicProfile");
    }

    [Theory]
    [InlineData("/api/challenges", "get")]
    [InlineData("/api/challenges", "post")]
    [InlineData("/api/challenges/join", "post")]
    [InlineData("/api/challenges/{challengeId}/leave", "delete")]
    [InlineData("/api/challenges/{challengeId}/habits", "put")]
    [InlineData("/api/challenges/{challengeId}", "get")]
    [InlineData("/api/accountability/pairs", "get")]
    [InlineData("/api/accountability/pairs", "post")]
    [InlineData("/api/accountability/pairs/{pairId}/accept", "post")]
    [InlineData("/api/accountability/pairs/{pairId}", "delete")]
    [InlineData("/api/accountability/pairs/{pairId}/habits", "put")]
    [InlineData("/api/accountability/pairs/{pairId}/check-ins", "get")]
    [InlineData("/api/accountability/pairs/{pairId}/check-ins", "post")]
    public void GeneratedOpenApi_RemovedRouteIsAbsent(string route, string method)
    {
        foreach (var file in new[] { "openapi.json", "openapi_v2.json" })
        {
            using var document = ReadDocument(file);
            document.RootElement.GetProperty("paths").TryGetProperty(route, out _).Should().BeFalse(
                $"{method} {route} must be absent from {file}");
        }
    }

    private static JsonDocument ReadDocument(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
