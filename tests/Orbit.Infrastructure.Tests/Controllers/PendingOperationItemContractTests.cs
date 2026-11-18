using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class PendingOperationItemContractTests
{
    [Fact]
    public void PreviewResponse_AppendsEmojiToItemAndChange()
    {
        var change = new PendingOperationChange(Guid.NewGuid(), "Brush teeth", "title", "Old", "New", "text",
            EntityEmoji: "🦷");
        var item = new PendingOperationItem("item", change.EntityId, change.EntityName, [change], "state",
            EntityEmoji: "🦷");

        var json = JsonSerializer.SerializeToElement(item, JsonSerializerOptions.Web);

        json.GetProperty("entityEmoji").GetString().Should().Be("🦷");
        json.GetProperty("fields")[0].GetProperty("entityEmoji").GetString().Should().Be("🦷");
        JsonSerializer.Deserialize<PendingOperationItem>(json, JsonSerializerOptions.Web).Should().BeEquivalentTo(item);
        var legacy = new PendingOperationChange(change.EntityId, change.EntityName, "title", "Old", "New", "text");
        JsonSerializer.SerializeToElement(legacy, JsonSerializerOptions.Web)
            .TryGetProperty("entityEmoji", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewResponse_AppendsRemovalFlagAndPreservesExistingItemFields(bool removesData)
    {
        var entityId = Guid.NewGuid();
        var fields = new[] { new PendingOperationChange(entityId, "Habit", "title", "Old", "New", "text") };
        var item = new PendingOperationItem("item", entityId, "Habit", fields, "state", removesData);
        var response = new PendingAgentOperation(Guid.NewGuid(), AgentCapabilityIds.HabitsBulkWrite,
            "Update", "Update habits", AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation,
            DateTime.UtcNow.AddMinutes(10), Items: [item]);

        var json = JsonSerializer.SerializeToElement(response, JsonSerializerOptions.Web)
            .GetProperty("items")[0];

        json.EnumerateObject().Select(property => property.Name).Should().Equal(
            "itemId", "entityId", "entityName", "fields", "stateFingerprint", "removesData");
        json.GetProperty("itemId").GetString().Should().Be("item");
        json.GetProperty("entityId").GetGuid().Should().Be(entityId);
        json.GetProperty("entityName").GetString().Should().Be("Habit");
        json.GetProperty("fields").GetRawText().Should()
            .Be(JsonSerializer.Serialize(fields, JsonSerializerOptions.Web));
        json.GetProperty("stateFingerprint").GetString().Should().Be("state");
        json.GetProperty("removesData").GetBoolean().Should().Be(removesData);
    }

    [Fact]
    public void LegacyItem_DeserializesWithoutRemovalFlag()
    {
        var item = JsonSerializer.Deserialize<PendingOperationItem>(
            """{"itemId":"0","entityId":null,"entityName":"Habit","fields":[],"stateFingerprint":"state"}""",
            JsonSerializerOptions.Web);

        item.Should().BeEquivalentTo(new PendingOperationItem("0", null, "Habit", [], "state"));
        item!.RemovesData.Should().BeNull();
        item.ValidationErrors.Should().BeNull();
        item.FieldOptions.Should().BeNull();
        item.Placement.Should().BeNull();
        JsonSerializer.SerializeToElement(item, JsonSerializerOptions.Web)
            .TryGetProperty("entityEmoji", out _).Should().BeFalse();
    }

    [Fact]
    public void EntityDetail_IsAnOptionalMemberThatAnItemWithoutADetailDoesNotWrite()
    {
        var plain = new PendingOperationItem("item", Guid.NewGuid(), "Water", [], "state");
        var detailed = plain with { EntityDetail = "Morning, 5 of 6" };

        JsonSerializer.SerializeToElement(plain, JsonSerializerOptions.Web).TryGetProperty("entityDetail", out _).Should().BeFalse();
        JsonSerializer.Serialize(plain).Should().NotContain("EntityDetail", "a stored preview and a hash input hold no new member");
        var json = JsonSerializer.SerializeToElement(detailed, JsonSerializerOptions.Web);
        json.GetProperty("entityDetail").GetString().Should().Be("Morning, 5 of 6");
        json.GetProperty("entityName").GetString().Should().Be("Water");
        JsonSerializer.Deserialize<PendingOperationItem>(json, JsonSerializerOptions.Web).Should().BeEquivalentTo(detailed);
        JsonSerializer.Deserialize<PendingOperationItem>(
            """{"itemId":"0","entityId":null,"entityName":"Water","fields":[],"stateFingerprint":"state"}""",
            JsonSerializerOptions.Web)!.EntityDetail.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"supportsPendingOperationEntityDetail":true}""", true)]
    [InlineData("""{"supportsPendingOperationEntityDetail":false}""", false)]
    [InlineData("""{"locale":"pt-BR"}""", null)]
    public void ClientContext_ReadsTheOptionalEntityDetailFlag(string value, bool? expected)
    {
        Orbit.Api.Controllers.ClientContextQuery.TryParse(value, out var context).Should().BeTrue();

        context!.SupportsPendingOperationEntityDetail.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(" ", true)]
    [InlineData("{", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    public void ClientContextQuery_TakesNoValueAndRefusesAValueThatIsNotAClientContext(string? value, bool valid)
    {
        Orbit.Api.Controllers.ClientContextQuery.TryParse(value, out var context).Should().Be(valid);

        context.Should().BeNull();
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void GeneratedOpenApi_HoldsTheOptionalEntityDetailTheFlagAndThePageParameter(string artifact)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", artifact));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var item = schemas.GetProperty("PendingOperationItem");
        item.GetProperty("properties").GetProperty("entityDetail").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
        item.GetProperty("required").EnumerateArray().Should().NotContain(field => field.GetString() == "entityDetail");
        var client = schemas.GetProperty("AgentClientContext");
        client.GetProperty("properties").GetProperty("supportsPendingOperationEntityDetail").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("boolean", "null");
        if (client.TryGetProperty("required", out var required))
            required.EnumerateArray().Should().NotContain(field => field.GetString() == "supportsPendingOperationEntityDetail");
        var page = document.RootElement.GetProperty("paths").EnumerateObject()
            .Single(route => route.Name.EndsWith("/conversations/latest", StringComparison.OrdinalIgnoreCase)).Value.GetProperty("get");
        var parameter = page.GetProperty("parameters").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "clientContext");
        parameter.GetProperty("in").GetString().Should().Be("query");
        (parameter.TryGetProperty("required", out var needed) && needed.GetBoolean()).Should().BeFalse();
        parameter.GetProperty("schema").GetProperty("type").GetString().Should().Be("string");
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void GeneratedOpenApi_AppendsOptionalNullableBooleanAndPreservesExistingSchema(string artifact)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", artifact));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var schema = schemas.GetProperty("PendingOperationItem");

        schema.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("itemId", "entityId", "entityName", "fields", "stateFingerprint");
        var properties = schema.GetProperty("properties");
        properties.GetProperty("entityEmoji").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
        properties.GetProperty("habitType").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
        properties.GetProperty("habitType").TryGetProperty("enum", out _).Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Should().NotContain(field => field.GetString() == "habitType");
        var change = schemas.GetProperty("PendingOperationChange");
        change.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("entityId", "entityName", "field", "oldValue", "newValue", "valueType");
        var changeProperties = change.GetProperty("properties");
        change.GetProperty("required").EnumerateArray().Should().NotContain(field => field.GetString() == "provenance" || field.GetString() == "removal");
        changeProperties.GetProperty("provenance").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
        changeProperties.EnumerateObject().Select(property => property.Name).Should().Equal(
            "entityId", "entityName", "field", "oldValue", "newValue", "valueType", "proposedValue", "isEditable", "entityEmoji", "provenance", "removal");
        changeProperties.GetProperty("entityEmoji").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
        properties.EnumerateObject().Select(property => property.Name).Should().Equal(
            "itemId", "entityId", "entityName", "fields", "stateFingerprint", "removesData", "validationErrors", "fieldOptions", "placement", "entityEmoji", "habitType", "entityDetail");
        properties.GetProperty("placement").GetProperty("oneOf").EnumerateArray()
            .Any(value => value.TryGetProperty("$ref", out var reference)
                && reference.GetString() == "#/components/schemas/PendingOperationPlacement").Should().BeTrue();
        schema.GetProperty("required").EnumerateArray().Should().NotContain(field => field.GetString() == "placement");
        properties.GetProperty("fieldOptions").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("array", "null");
        properties.GetProperty("fieldOptions").GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/PendingOperationFieldOptions");
        properties.GetProperty("fieldOptions").GetProperty("description").GetString().Should().Be(
            "Only present for a required unresolved parent choice. Options contain exactly the matching parent habits and No parent; resolved or root items omit this field.");
        var choice = schemas.GetProperty("PendingOperationFieldOptions");
        choice.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("field", "required", "options");
        var choiceProperties = choice.GetProperty("properties");
        choiceProperties.EnumerateObject().Select(property => property.Name).Should().Equal("field", "required", "options");
        choiceProperties.GetProperty("field").GetProperty("type").GetString().Should().Be("string");
        choiceProperties.GetProperty("required").GetProperty("type").GetString().Should().Be("boolean");
        choiceProperties.GetProperty("options").GetProperty("type").GetString().Should().Be("array");
        choiceProperties.GetProperty("options").GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/PendingOperationFieldOption");
        var option = schemas.GetProperty("PendingOperationFieldOption");
        option.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("value", "label");
        var optionProperties = option.GetProperty("properties");
        optionProperties.EnumerateObject().Select(property => property.Name).Should().Equal("value", "label");
        optionProperties.GetProperty("value").GetProperty("$ref").GetString().Should().Be("#/components/schemas/JsonElement");
        optionProperties.GetProperty("label").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("validationErrors").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("array", "null");
        properties.GetProperty("validationErrors").GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/PendingOperationValidationError");
        properties.GetProperty("removesData").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("boolean", "null");
        properties.GetProperty("itemId").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("entityId").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
        properties.GetProperty("entityId").GetProperty("format").GetString().Should().Be("uuid");
        properties.GetProperty("entityName").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("fields").GetProperty("type").GetString().Should().Be("array");
        properties.GetProperty("fields").GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/PendingOperationChange");
        properties.GetProperty("stateFingerprint").GetProperty("type").GetString().Should().Be("string");
    }
}
