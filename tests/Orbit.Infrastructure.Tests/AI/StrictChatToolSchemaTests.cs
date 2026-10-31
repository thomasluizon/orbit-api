#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Xunit.Abstractions;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using OpenAI.Responses;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public sealed class StrictChatToolSchemaTests(ITestOutputHelper output)
{
    [Fact]
    public void AllChatDeclarations_StayWithinProviderNestingLimit()
    {
        var violations = new List<string>();
        foreach (var tool in new AiToolRegistry(AllTools()).GetAll().OrderBy(tool => tool.Name))
            foreach (var offers in new[] { false, true })
                foreach (var (variant, source) in DeclarationVariants(tool))
                {
                    var schema = offers && ProactiveProposal.Supports(tool.Name) ? ProactiveProposal.Schema(source) : source;
                    if (tool.Name == "revise_pending_changes") schema = ProactiveProposal.RevisionSchema(schema, offers);
                    schema = tool.IsReadOnly ? TerminalReadDirective.WithCompletionArgument(schema)
                        : TerminalWriteDirective.WithCompletionArgument(schema);
                    var method = typeof(AiIntentService).GetMethod("ConvertToSdkTool", BindingFlags.NonPublic | BindingFlags.Static)!;
                    var sdkTool = (ResponseTool)method.Invoke(null, [new { name = tool.Name, parameters = schema }])!;
                    using var wire = JsonDocument.Parse(ModelReaderWriter.Write(sdkTool).ToString());
                    if (tool.Name == "create_tag" && offers) output.WriteLine("SDK wire evidence: " + wire.RootElement.GetRawText());
                    var parameters = wire.RootElement.GetProperty("parameters");
                    AssertStrict(parameters);
                    var depth = ObjectDepth(parameters);
                    output.WriteLine($"{tool.Name} | {variant} | {(offers ? "offers" : "plain")} | {depth}");
                    if (depth > 10) violations.Add($"{tool.Name}/{variant}/{(offers ? "offers" : "plain")}: {depth}");
                }
        violations.Should().BeEmpty("the provider permits at most 10 object levels; the production bulk_create_habits rejection reports 12");
    }

    private static IEnumerable<(string Variant, object Schema)> DeclarationVariants(IAiTool tool)
    {
        yield return ("initial", tool.GetParameterSchema());
        if (tool is not AskTool ask) yield break;
        yield return ("post-read", ask.GetParameterSchema(true));
        yield return ("draft-revision", ask.GetParameterSchema(false, [new HabitSetupDraft("draft", "Activity", "frequency_unit", Guid.NewGuid())]));
    }

    private static int ObjectDepth(JsonElement schema)
    {
        var own = schema.TryGetProperty("properties", out var properties) ? 1 : 0;
        var children = new List<int> { 0 };
        if (own != 0) children.AddRange(properties.EnumerateObject().Select(property => ObjectDepth(property.Value)));
        if (schema.TryGetProperty("items", out var items)) children.Add(ObjectDepth(items));
        if (schema.TryGetProperty("anyOf", out var alternatives)) children.AddRange(alternatives.EnumerateArray().Select(ObjectDepth));
        return own + children.Max();
    }

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
            AssertNoEnumWithPattern(wire.RootElement.GetProperty("parameters"), tool.Name);
            AssertOptionalFields(JsonSerializer.SerializeToElement(schema), wire.RootElement.GetProperty("parameters"));
        }
    }

    [Fact]
    public void Ask_WireSchemaRequiresNonemptyContentForEveryForm()
    {
        var tool = AllTools().Single(tool => tool is AskTool);
        var method = typeof(AiIntentService).GetMethod("ConvertToSdkTool", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sdkTool = (ResponseTool)method.Invoke(null, [new { name = tool.Name, description = tool.Description, parameters = tool.GetParameterSchema() }])!;
        using var wire = JsonDocument.Parse(ModelReaderWriter.Write(sdkTool).ToString());
        output.WriteLine(wire.RootElement.GetRawText());
        var schema = wire.RootElement.GetProperty("parameters");
        AssertStrict(schema);
        schema.GetProperty("properties").TryGetProperty("input", out var input).Should().BeTrue(
            "each ask form needs its own non-null required fields inside a nested union");
        var forms = input.GetProperty("anyOf").EnumerateArray().ToArray();
        forms.Should().HaveCount(3);
        foreach (var (form, required) in new (string, string[])[]
                 { ("choice", ["form", "question", "options"]), ("pickList", ["form", "title", "action", "items"]), ("question", ["form", "questions"]) })
        {
            var fields = forms.Single(candidate => candidate.GetProperty("properties").GetProperty("form")
                .GetProperty("enum")[0].GetString() == form).GetProperty("properties");
            foreach (var name in required)
                fields.GetProperty(name).GetProperty("type").ValueKind.Should().Be(JsonValueKind.String,
                    $"{form}.{name} must never become nullable in the provider schema");
            if (form == "choice") fields.GetProperty("question").GetProperty("pattern").GetString().Should().Be(@"\S");
            if (form == "pickList")
            {
                fields.GetProperty("title").GetProperty("pattern").GetString().Should().Be(@"\S");
                fields.GetProperty("items").GetProperty("minItems").GetInt32().Should().Be(1);
            }
            if (form == "question")
            {
                fields.GetProperty("questions").GetProperty("minItems").GetInt32().Should().Be(1);
                foreach (var question in fields.GetProperty("questions").GetProperty("items").GetProperty("anyOf").EnumerateArray())
                {
                    question.GetProperty("properties").GetProperty("prompt").GetProperty("pattern").GetString().Should().Be(@"\S");
                    foreach (var field in question.GetProperty("properties").EnumerateObject()
                                 .Where(field => field.Name != "allowFreeText"))
                        field.Value.GetProperty("type").ValueKind.Should().Be(JsonValueKind.String);
                }
            }
        }
    }

    [Fact]
    public void SuggestHabits_WireSchemaRequiresExplicitBoundedCount()
    {
        var tool = new SuggestHabitsTool(Substitute.For<MediatR.IMediator>());
        var source = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        var method = typeof(AiIntentService).GetMethod("ConvertToSdkTool", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sdkTool = (ResponseTool)method.Invoke(null, [new { name = tool.Name, parameters = source }])!;
        using var wire = JsonDocument.Parse(ModelReaderWriter.Write(sdkTool).ToString());
        var schema = wire.RootElement.GetProperty("parameters");
        var count = schema.GetProperty("properties").GetProperty("count");
        output.WriteLine(wire.RootElement.GetRawText());

        wire.RootElement.GetProperty("strict").GetBoolean().Should().BeTrue();
        count.GetProperty("type").ValueKind.Should().Be(JsonValueKind.String, "count must never allow null");
        count.GetProperty("type").GetString().Should().Be("integer");
        source.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Contain("count");
        count.GetProperty("minimum").GetInt32().Should().Be(1);
        count.GetProperty("maximum").GetInt32().Should().Be(HabitSuggestionsCard.MaxDrafts);
        count.GetProperty("description").GetString().Should().Be(
            "Number of habit drafts to return, from 1 to 5. Use the number requested by the person.");
        AssertStrict(schema);
    }

    [Fact]
    public void StartingStep_WireSchemaRequiresAnAction()
    {
        var tool = new SuggestBreakdownTool();
        var method = typeof(AiIntentService).GetMethod("ConvertToSdkTool", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sdkTool = (ResponseTool)method.Invoke(null, [new { name = tool.Name, parameters = tool.GetParameterSchema() }])!;
        using var wire = JsonDocument.Parse(ModelReaderWriter.Write(sdkTool).ToString());
        var schema = wire.RootElement.GetProperty("parameters");
        output.WriteLine(wire.RootElement.GetRawText());
        var steps = schema.GetProperty("properties").GetProperty("suggested_sub_habits");
        steps.TryGetProperty("minItems", out var minimum).Should().BeTrue();
        minimum.GetInt32().Should().Be(1);
        steps.GetProperty("items").GetProperty("properties").GetProperty("title")
            .TryGetProperty("pattern", out _).Should().BeTrue();
        AssertStrict(schema);
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

    private static void AssertNoEnumWithPattern(JsonElement node, string path)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("enum", out _))
                node.TryGetProperty("pattern", out _).Should().BeFalse(
                    $"provider-facing schema {path} must not combine enum and pattern: {node.GetRawText()}");
            foreach (var property in node.EnumerateObject())
                AssertNoEnumWithPattern(property.Value, $"{path}.{property.Name}");
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in node.EnumerateArray())
                AssertNoEnumWithPattern(child, $"{path}[{index++}]");
        }
    }

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
