#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Xunit.Abstractions;
using NSubstitute;
using Orbit.Domain.Interfaces;
using OpenAI.Responses;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public sealed class StrictChatToolSchemaTests(ITestOutputHelper output)
{
    [Fact]
    public void RegistryTools_SerializeWithStrictSchemas()
    {
        var tools = AllTools();
        var registry = new AiToolRegistry(tools);
        registry.GetAll().Should().NotBeEmpty();
        output.WriteLine($"Chat tools: {registry.GetAll().Count}");
        foreach (var tool in registry.GetAll())
        {
            var schema = tool.GetParameterSchema();
            AssertLowercaseTypes(JsonSerializer.SerializeToElement(schema));
            var method = typeof(AiIntentService).GetMethod("ConvertToSdkTool", BindingFlags.NonPublic | BindingFlags.Static)!;
            var sdkTool = (ResponseTool)method.Invoke(null, [new { name = tool.Name, description = tool.Description, parameters = schema }])!;
            using var wire = JsonDocument.Parse(ModelReaderWriter.Write(sdkTool).ToString());
            wire.RootElement.GetProperty("strict").GetBoolean().Should().BeTrue(tool.Name);
            AssertStrict(wire.RootElement.GetProperty("parameters"));
            AssertOptionalFields(JsonSerializer.SerializeToElement(schema), wire.RootElement.GetProperty("parameters"));
        }
    }

    [Fact]
    public void Invariant_RejectsMissingRequiredProperty()
    {
        using var malformed = JsonDocument.Parse("""
            {"type":"object","properties":{"title":{"type":"string"}},"additionalProperties":false,"required":[]}
            """);
        var check = () => AssertStrict(malformed.RootElement);
        check.Should().Throw<Xunit.Sdk.XunitException>();
    }

    [Theory]
    [InlineData("maxLength")]
    [InlineData("minLength")]
    [InlineData("nullable")]
    [InlineData("sensitive")]
    [InlineData("allOf")]
    [InlineData("not")]
    [InlineData("dependentRequired")]
    [InlineData("dependentSchemas")]
    [InlineData("if")]
    [InlineData("then")]
    [InlineData("else")]
    [InlineData("patternProperties")]
    [InlineData("uniqueItems")]
    [InlineData("default")]
    public void Invariant_RejectsUnsupportedKeyword(string keyword)
    {
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["type"] = "string", [keyword] = 1 });
        var check = () => AssertStrict(schema);
        check.Should().Throw<Xunit.Sdk.XunitException>();
    }

    [Fact]
    public void Invariant_RejectsObjectWithoutProperties()
    {
        var check = () => AssertStrict(JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}"""));
        check.Should().Throw<Xunit.Sdk.XunitException>();
    }

    [Theory]
    [InlineData("maxLength")]
    [InlineData("minLength")]
    [InlineData("allOf")]
    [InlineData("default")]
    [InlineData("patternProperties")]
    [InlineData("unknownKeyword")]
    public void Conversion_StripsUnsupportedKeywordsAndPreservesInternalRules(string keyword)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["value"] = new Dictionary<string, object> { ["type"] = "string", [keyword] = 3 }
            }
        });
        var method = typeof(AiIntentService).GetMethod("ConvertToSdkTool", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tool = (ResponseTool)method.Invoke(null, [new { name = "keyword_test", parameters = schema }])!;
        using var wire = JsonDocument.Parse(ModelReaderWriter.Write(tool).ToString());
        AssertStrict(wire.RootElement.GetProperty("parameters"));
        schema.GetProperty("properties").GetProperty("value").GetProperty(keyword).GetInt32().Should().Be(3);
    }

    [Fact]
    public void InstalledSdk_ExposesNullableBooleanStrictMode()
    {
        var method = typeof(ResponseTool).GetMethods().Single(method => method.Name == "CreateFunctionTool");
        foreach (var parameter in method.GetParameters()) output.WriteLine($"{parameter.Name}: {parameter.ParameterType}");
        var strict = method.GetParameters().Single(parameter => parameter.Name == "strictModeEnabled");
        strict.ParameterType.Should().Be(typeof(bool?));
    }

    [Fact]
    public void Conversion_StripsOnlySchemaMetadataAndPreservesInternalSchema()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","sensitive":true,"properties":{
              "sensitive":{"type":"string","nullable":true,"sensitive":true,"enum":["secret"]},
              "items":{"type":"array","items":{"type":"object","properties":{"value":{"type":"string","sensitive":true}}}}
            },"required":["sensitive"]}
            """);
        var method = typeof(AiIntentService).GetMethod("ConvertToSdkTool", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tool = (ResponseTool)method.Invoke(null, [new { name = "metadata_test", parameters = schema }])!;
        using var wire = JsonDocument.Parse(ModelReaderWriter.Write(tool).ToString());
        var parameters = wire.RootElement.GetProperty("parameters");
        AssertStrict(parameters);
        parameters.GetProperty("properties").GetProperty("sensitive").GetProperty("enum")
            .EnumerateArray().Select(value => value.ValueKind).Should().Contain(JsonValueKind.Null);
        schema.GetProperty("sensitive").GetBoolean().Should().BeTrue();
        schema.GetProperty("properties").GetProperty("sensitive").GetProperty("nullable").GetBoolean().Should().BeTrue();
    }

    private static void AssertOptionalFields(JsonElement source, JsonElement wire)
    {
        if (source.TryGetProperty("properties", out var properties))
        {
            var required = source.TryGetProperty("required", out var names)
                ? names.EnumerateArray().Select(name => name.GetString()).ToHashSet() : [];
            foreach (var property in properties.EnumerateObject())
            {
                var normalized = wire.GetProperty("properties").GetProperty(property.Name);
                var optional = !required.Contains(property.Name) || (property.Value.TryGetProperty("nullable", out var marker) && marker.GetBoolean());
                if (optional)
                {
                    if (normalized.TryGetProperty("type", out var types))
                        types.EnumerateArray().Select(type => type.GetString()).Should().Contain("null");
                    else
                        normalized.GetProperty("anyOf").EnumerateArray()
                            .Should().Contain(alternative => alternative.GetProperty("type").GetString() == "null");
                    if (normalized.TryGetProperty("enum", out var options))
                        options.EnumerateArray().Should().Contain(option => option.ValueKind == JsonValueKind.Null);
                }
                AssertOptionalFields(property.Value, normalized);
            }
        }
        if (source.TryGetProperty("items", out var items)) AssertOptionalFields(items, wire.GetProperty("items"));
        if (source.TryGetProperty("anyOf", out var alternatives))
            foreach (var pair in alternatives.EnumerateArray().Zip(wire.GetProperty("anyOf").EnumerateArray()))
                AssertOptionalFields(pair.First, pair.Second);
    }

    internal static IReadOnlyList<IAiTool> AllTools() =>
        new[] { typeof(IAiTool).Assembly, typeof(AiIntentService).Assembly, typeof(Orbit.Api.Controllers.AiController).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IAiTool).IsAssignableFrom(type))
            .Select(CreateTool).ToArray();

    internal static IAiTool CreateTool(Type type) => type == typeof(AskTool)
        ? new AskTool((BulkCreateHabitsTool)RuntimeHelpers.GetUninitializedObject(typeof(BulkCreateHabitsTool)), (BulkLogHabitsTool)RuntimeHelpers.GetUninitializedObject(typeof(BulkLogHabitsTool)),
            (BulkSkipHabitsTool)RuntimeHelpers.GetUninitializedObject(typeof(BulkSkipHabitsTool)), Substitute.For<IUserDateService>())
        : (IAiTool)RuntimeHelpers.GetUninitializedObject(type);

    private static void AssertLowercaseTypes(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray()) AssertLowercaseTypes(child);
        }
        if (node.ValueKind != JsonValueKind.Object) return;
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == "type" && property.Value.ValueKind == JsonValueKind.String)
                property.Value.GetString().Should().Be(property.Value.GetString()!.ToLowerInvariant());
            AssertLowercaseTypes(property.Value);
        }
    }

    internal static void AssertStrict(JsonElement schema)
    {
        string[] supported = ["type", "description", "title", "properties", "required", "additionalProperties",
            "items", "enum", "anyOf", "$defs", "$ref", "pattern", "format", "multipleOf", "maximum",
            "exclusiveMaximum", "minimum", "exclusiveMinimum", "minItems", "maxItems"];
        foreach (var keyword in schema.EnumerateObject())
            supported.Should().Contain(keyword.Name, $"unsupported keyword {keyword.Name}");
        if (schema.TryGetProperty("type", out var objectType)
            && (objectType.ValueKind == JsonValueKind.String && objectType.GetString() == "object"
                || objectType.ValueKind == JsonValueKind.Array && objectType.EnumerateArray().Any(type => type.GetString() == "object")))
            schema.TryGetProperty("properties", out _).Should().BeTrue("objects must declare properties");
        if (schema.TryGetProperty("properties", out var properties))
        {
            schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
            schema.GetProperty("required").EnumerateArray().Select(value => value.GetString())
                .Should().BeEquivalentTo(properties.EnumerateObject().Select(property => property.Name));
            foreach (var property in properties.EnumerateObject()) AssertStrict(property.Value);
        }
        if (schema.TryGetProperty("type", out var types))
        {
            var values = types.ValueKind == JsonValueKind.Array ? types.EnumerateArray().ToArray() : [types];
            foreach (var type in values)
                type.GetString().Should().BeOneOf("string", "number", "integer", "boolean", "object", "array", "null");
        }
        if (schema.TryGetProperty("$defs", out var definitions))
            foreach (var definition in definitions.EnumerateObject()) AssertStrict(definition.Value);
        if (schema.TryGetProperty("items", out var items)) AssertStrict(items);
        if (schema.TryGetProperty("anyOf", out var alternatives))
            foreach (var alternative in alternatives.EnumerateArray()) AssertStrict(alternative);
    }
}
