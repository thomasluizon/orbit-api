using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AppleContractTests
{
    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void PublishedSchemas_AppendOptionalBillingProviderAndPreserveNullableLegacySources(string name)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", name));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var (schemaName, legacySource) in new[] { ("ProfileResponse", "subscriptionSource"), ("SubscriptionStatusResponse", "source") })
        {
            var schema = schemas.GetProperty(schemaName);
            var properties = schema.GetProperty("properties");
            var provider = properties.GetProperty("billingProvider");
            provider.GetProperty("type").EnumerateArray().Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
            properties.GetProperty(legacySource).GetProperty("type").EnumerateArray().Select(type => type.GetString())
                .Should().Contain("null");
            if (schema.TryGetProperty("required", out var required))
                required.EnumerateArray().Select(value => value.GetString()).Should().NotContain("billingProvider");
        }
        var status = document.RootElement.GetProperty("paths").GetProperty("/api/subscriptions/status").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        status.GetProperty("$ref").GetString().Should().Be("#/components/schemas/SubscriptionStatusResponse");
    }
}
