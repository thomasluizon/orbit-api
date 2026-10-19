using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public class SocialDeprecationTests
{
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
    public void GeneratedOpenApi_RouteRemainsAvailableAndDeprecated(string route, string method)
    {
        foreach (var file in new[] { "openapi.json", "openapi_v2.json" })
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../..", "src/Orbit.Api", file));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var operation = document.RootElement.GetProperty("paths").GetProperty(route).GetProperty(method);

            operation.TryGetProperty("deprecated", out var deprecated).Should().BeTrue(
                $"{method} {route} must be deprecated in {file}");
            deprecated.GetBoolean().Should().BeTrue();
        }
    }
}
