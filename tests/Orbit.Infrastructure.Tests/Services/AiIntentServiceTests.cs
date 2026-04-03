using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure helper methods of AiIntentService:
/// NormalizeSchemaTypes, ConvertToSdkTool, and SerializeOptions behavior.
/// The AI client interactions require mocking the ChatClient which
/// is complex; we test the deterministic logic here.
/// </summary>
public class AiIntentServiceTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    // ── NormalizeSchemaTypes ──

    [Fact]
    public void NormalizeSchemaTypes_UppercaseTypes_ConvertsToLowercase()
    {
        var input = """{"type": "OBJECT", "properties": {"name": {"type": "STRING"}}}""";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Contain("\"object\"");
        result.Should().Contain("\"string\"");
        result.Should().NotContain("\"OBJECT\"");
        result.Should().NotContain("\"STRING\"");
    }

    [Fact]
    public void NormalizeSchemaTypes_LowercaseTypes_RemainsUnchanged()
    {
        var input = """{"type": "object", "items": {"type": "string"}}""";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Be(input);
    }

    [Fact]
    public void NormalizeSchemaTypes_MixedTypes_NormalizesAll()
    {
        var input = """{"type": "ARRAY", "items": {"type": "NUMBER"}, "meta": {"type": "BOOLEAN"}, "id": {"type": "INTEGER"}}""";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Contain("\"array\"");
        result.Should().Contain("\"number\"");
        result.Should().Contain("\"boolean\"");
        result.Should().Contain("\"integer\"");
    }

    [Fact]
    public void NormalizeSchemaTypes_NoTypeFields_RemainsUnchanged()
    {
        var input = """{"name": "test", "value": 42}""";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Be(input);
    }

    [Fact]
    public void NormalizeSchemaTypes_EmptyJson_RemainsUnchanged()
    {
        var input = "{}";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Be("{}");
    }

    [Theory]
    [InlineData("OBJECT", "object")]
    [InlineData("STRING", "string")]
    [InlineData("ARRAY", "array")]
    [InlineData("NUMBER", "number")]
    [InlineData("BOOLEAN", "boolean")]
    [InlineData("INTEGER", "integer")]
    public void NormalizeSchemaTypes_EachType_NormalizesIndividually(string uppercase, string expected)
    {
        var input = "{\"type\": \"" + uppercase + "\"}";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Contain($"\"{expected}\"");
    }

    [Fact]
    public void NormalizeSchemaTypes_TypeInValue_NotInTypeField_Unchanged()
    {
        // The word OBJECT appears as a value, not in a "type" field
        var input = """{"name": "OBJECT", "desc": "STRING value"}""";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Be(input);
    }

    [Fact]
    public void NormalizeSchemaTypes_DeeplyNested_NormalizesAll()
    {
        var input = """{"type": "OBJECT", "properties": {"items": {"type": "ARRAY", "items": {"type": "OBJECT", "properties": {"value": {"type": "NUMBER"}}}}}}""";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().NotContain("OBJECT");
        result.Should().NotContain("ARRAY");
        result.Should().NotContain("NUMBER");
        result.Should().Contain("\"object\"");
        result.Should().Contain("\"array\"");
        result.Should().Contain("\"number\"");
    }

    [Fact]
    public void NormalizeSchemaTypes_ExtraWhitespace_StillMatches()
    {
        // The regex handles optional whitespace around the colon
        var input = """{"type"  :  "STRING"}""";
        var result = InvokeNormalizeSchemaTypes(input);

        result.Should().Contain("\"string\"");
    }

    // ── ConvertToSdkTool ──

    [Fact]
    public void ConvertToSdkTool_ValidDeclaration_ReturnsChatTool()
    {
        var declaration = new
        {
            name = "create_habit",
            description = "Creates a new habit",
            parameters = new
            {
                type = "OBJECT",
                properties = new
                {
                    title = new { type = "STRING", description = "Habit title" }
                }
            }
        };

        var result = InvokeConvertToSdkTool(declaration);

        result.Should().NotBeNull();
    }

    [Fact]
    public void ConvertToSdkTool_MinimalDeclaration_NoParameters_ReturnsTool()
    {
        var declaration = new { name = "list_habits" };

        var result = InvokeConvertToSdkTool(declaration);

        result.Should().NotBeNull();
    }

    [Fact]
    public void ConvertToSdkTool_WithDescription_ReturnsTool()
    {
        var declaration = new
        {
            name = "delete_habit",
            description = "Deletes a habit by ID"
        };

        var result = InvokeConvertToSdkTool(declaration);

        result.Should().NotBeNull();
    }

    // ── SerializeOptions ──

    [Fact]
    public void SerializeOptions_UsesCamelCase()
    {
        var field = typeof(AiIntentService)
            .GetField("SerializeOptions", PrivateStatic)!;
        var options = (JsonSerializerOptions)field.GetValue(null)!;

        options.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
    }

    // ── Helpers ──

    private static string InvokeNormalizeSchemaTypes(string json)
    {
        var method = typeof(AiIntentService)
            .GetMethod("NormalizeSchemaTypes", PrivateStatic)!;
        return (string)method.Invoke(null, [json])!;
    }

    private static object? InvokeConvertToSdkTool(object declaration)
    {
        var method = typeof(AiIntentService)
            .GetMethod("ConvertToSdkTool", PrivateStatic)!;
        return method.Invoke(null, [declaration]);
    }
}
