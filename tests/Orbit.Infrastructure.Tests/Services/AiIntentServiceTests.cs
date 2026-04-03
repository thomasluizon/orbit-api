using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure helper methods of AiIntentService:
/// NormalizeSchemaTypes (via reflection since private static).
/// The AI client interactions require mocking the ChatClient which
/// is complex; we test the deterministic logic here.
/// </summary>
public class AiIntentServiceTests
{
    [Fact]
    public void NormalizeSchemaTypes_UppercaseTypes_ConvertsToLowercase()
    {
        var method = typeof(AiIntentService)
            .GetMethod("NormalizeSchemaTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var input = """{"type": "OBJECT", "properties": {"name": {"type": "STRING"}}}""";
        var result = (string)method.Invoke(null, [input])!;

        result.Should().Contain("\"object\"");
        result.Should().Contain("\"string\"");
        result.Should().NotContain("\"OBJECT\"");
        result.Should().NotContain("\"STRING\"");
    }

    [Fact]
    public void NormalizeSchemaTypes_LowercaseTypes_RemainsUnchanged()
    {
        var method = typeof(AiIntentService)
            .GetMethod("NormalizeSchemaTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var input = """{"type": "object", "items": {"type": "string"}}""";
        var result = (string)method.Invoke(null, [input])!;

        result.Should().Be(input);
    }

    [Fact]
    public void NormalizeSchemaTypes_MixedTypes_NormalizesAll()
    {
        var method = typeof(AiIntentService)
            .GetMethod("NormalizeSchemaTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var input = """{"type": "ARRAY", "items": {"type": "NUMBER"}, "meta": {"type": "BOOLEAN"}, "id": {"type": "INTEGER"}}""";
        var result = (string)method.Invoke(null, [input])!;

        result.Should().Contain("\"array\"");
        result.Should().Contain("\"number\"");
        result.Should().Contain("\"boolean\"");
        result.Should().Contain("\"integer\"");
    }

    [Fact]
    public void NormalizeSchemaTypes_NoTypeFields_RemainsUnchanged()
    {
        var method = typeof(AiIntentService)
            .GetMethod("NormalizeSchemaTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var input = """{"name": "test", "value": 42}""";
        var result = (string)method.Invoke(null, [input])!;

        result.Should().Be(input);
    }

    [Fact]
    public void NormalizeSchemaTypes_EmptyJson_RemainsUnchanged()
    {
        var method = typeof(AiIntentService)
            .GetMethod("NormalizeSchemaTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var input = "{}";
        var result = (string)method.Invoke(null, [input])!;

        result.Should().Be("{}");
    }
}
