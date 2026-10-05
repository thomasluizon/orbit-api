using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class PendingOperationItemContractTests
{
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
    }

    [Fact]
    public void GeneratedOpenApi_AppendsOptionalNullableBooleanAndPreservesExistingSchema()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api/openapi.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("PendingOperationItem");

        schema.GetProperty("required").EnumerateArray().Select(field => field.GetString())
            .Should().BeEquivalentTo("itemId", "entityId", "entityName", "fields", "stateFingerprint");
        var properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(property => property.Name).Should().Equal(
            "itemId", "entityId", "entityName", "fields", "stateFingerprint", "removesData", "validationErrors");
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
