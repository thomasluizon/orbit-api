using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public class SocialDeprecationTests
{
    public static IEnumerable<object[]> RetiringRoutes()
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

        foreach (var file in new[] { "openapi.json", "openapi_v2.json" })
            foreach (var (path, method) in routes)
                yield return [file, path, method];
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

    private static JsonDocument ReadDocument(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
