using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using Orbit.Api.Controllers;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AiMemoryDeprecationTests
{
    [Theory]
    [InlineData(typeof(UserFactsController), "GetUserFacts", null, "GET")]
    [InlineData(typeof(UserFactsController), "DeleteUserFact", "{id:guid}", "DELETE")]
    [InlineData(typeof(UserFactsController), "BulkDeleteUserFacts", "bulk", "DELETE")]
    [InlineData(typeof(ProfileController), "SetAiMemory", "ai-memory", "PUT")]
    public void RouteRemainsAvailableAndDeprecated(Type controller, string action, string? template, string verb)
    {
        var method = controller.GetMethod(action);

        method.Should().NotBeNull();
        var route = method!.GetCustomAttributes<HttpMethodAttribute>().Should().ContainSingle().Subject;
        route.Template.Should().Be(template);
        route.HttpMethods.Should().ContainSingle().Which.Should().Be(verb);
        method.GetCustomAttributes<ObsoleteAttribute>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("openapi.json", "/api/user-facts", "get")]
    [InlineData("openapi.json", "/api/user-facts/{id}", "delete")]
    [InlineData("openapi.json", "/api/user-facts/bulk", "delete")]
    [InlineData("openapi.json", "/api/Profile/ai-memory", "put")]
    [InlineData("openapi_v2.json", "/api/user-facts", "get")]
    [InlineData("openapi_v2.json", "/api/user-facts/{id}", "delete")]
    [InlineData("openapi_v2.json", "/api/user-facts/bulk", "delete")]
    [InlineData("openapi_v2.json", "/api/Profile/ai-memory", "put")]
    public void GeneratedOpenApiMarksRouteDeprecated(string file, string route, string verb)
    {
        using var document = ReadOpenApi(file);
        var operation = document.RootElement.GetProperty("paths").GetProperty(route).GetProperty(verb);

        operation.TryGetProperty("deprecated", out var deprecated).Should().BeTrue();
        deprecated.GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void GeneratedOpenApiKeepsMemorySettingRequiredAndMarksItDeprecated(string file)
    {
        using var document = ReadOpenApi(file);
        var profile = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProfileResponse");
        var setting = profile.GetProperty("properties").GetProperty("aiMemoryEnabled");

        profile.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().Contain("aiMemoryEnabled");
        setting.GetProperty("type").GetString().Should().Be("boolean");
        setting.TryGetProperty("deprecated", out var deprecated).Should().BeTrue();
        deprecated.GetBoolean().Should().BeTrue();
    }

    private static JsonDocument ReadOpenApi(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
