using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class ChecklistTemplateTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly IReadOnlyList<string> ValidItems = ["Item 1", "Item 2", "Item 3"];

    private static ChecklistTemplate CreateValidTemplate(
        string name = "Morning Routine",
        IReadOnlyList<string>? items = null)
    {
        var result = ChecklistTemplate.Create(ValidUserId, name, items ?? ValidItems);
        return result.Value;
    }

    // --- Create tests ---

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = ChecklistTemplate.Create(ValidUserId, "Morning Routine", ValidItems);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(ValidUserId);
        result.Value.Name.Should().Be("Morning Routine");
        result.Value.Items.Should().HaveCount(3);
        result.Value.Items.Should().ContainInOrder("Item 1", "Item 2", "Item 3");
    }

    [Fact]
    public void Create_EmptyUserId_ReturnsFailure()
    {
        var result = ChecklistTemplate.Create(Guid.Empty, "Template", ValidItems);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyName_ReturnsFailure()
    {
        var result = ChecklistTemplate.Create(ValidUserId, "", ValidItems);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Template name is required");
    }

    [Fact]
    public void Create_WhitespaceName_ReturnsFailure()
    {
        var result = ChecklistTemplate.Create(ValidUserId, "   ", ValidItems);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Template name is required");
    }

    [Fact]
    public void Create_NameOver100Chars_ReturnsFailure()
    {
        var longName = new string('a', 101);

        var result = ChecklistTemplate.Create(ValidUserId, longName, ValidItems);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("100 characters or less");
    }

    [Fact]
    public void Create_NameExactly100Chars_ReturnsSuccess()
    {
        var name = new string('a', 100);

        var result = ChecklistTemplate.Create(ValidUserId, name, ValidItems);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_EmptyItems_ReturnsFailure()
    {
        var result = ChecklistTemplate.Create(ValidUserId, "Template", []);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("At least one item is required");
    }

    [Fact]
    public void Create_TrimsName()
    {
        var result = ChecklistTemplate.Create(ValidUserId, "  Morning Routine  ", ValidItems);

        result.Value.Name.Should().Be("Morning Routine");
    }

    [Fact]
    public void Create_TrimsItems()
    {
        var items = new List<string> { "  Item 1  ", "  Item 2  " };

        var result = ChecklistTemplate.Create(ValidUserId, "Template", items);

        result.Value.Items.Should().ContainInOrder("Item 1", "Item 2");
    }

    [Fact]
    public void Create_FiltersOutEmptyItems()
    {
        var items = new List<string> { "Item 1", "", "  ", "Item 2" };

        var result = ChecklistTemplate.Create(ValidUserId, "Template", items);

        result.Value.Items.Should().HaveCount(2);
        result.Value.Items.Should().ContainInOrder("Item 1", "Item 2");
    }

    [Fact]
    public void Create_SingleItem_ReturnsSuccess()
    {
        var result = ChecklistTemplate.Create(ValidUserId, "Template", ["Single item"]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.Should().Be("Single item");
    }

    [Fact]
    public void Create_SetsCreatedAtUtc()
    {
        var before = DateTime.UtcNow;

        var result = ChecklistTemplate.Create(ValidUserId, "Template", ValidItems);

        var after = DateTime.UtcNow;
        result.Value.CreatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // --- Update tests ---

    [Fact]
    public void Update_ValidInput_UpdatesFields()
    {
        var template = CreateValidTemplate();
        var newItems = new List<string> { "New Item 1", "New Item 2" };

        var result = template.Update("Updated Name", newItems);

        result.IsSuccess.Should().BeTrue();
        template.Name.Should().Be("Updated Name");
        template.Items.Should().HaveCount(2);
        template.Items.Should().ContainInOrder("New Item 1", "New Item 2");
    }

    [Fact]
    public void Update_EmptyName_ReturnsFailure()
    {
        var template = CreateValidTemplate();

        var result = template.Update("", ValidItems);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Template name is required");
    }

    [Fact]
    public void Update_WhitespaceName_ReturnsFailure()
    {
        var template = CreateValidTemplate();

        var result = template.Update("   ", ValidItems);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Template name is required");
    }

    [Fact]
    public void Update_NameOver100Chars_ReturnsFailure()
    {
        var template = CreateValidTemplate();
        var longName = new string('a', 101);

        var result = template.Update(longName, ValidItems);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("100 characters or less");
    }

    [Fact]
    public void Update_EmptyItems_ReturnsFailure()
    {
        var template = CreateValidTemplate();

        var result = template.Update("Name", []);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("At least one item is required");
    }

    [Fact]
    public void Update_TrimsName()
    {
        var template = CreateValidTemplate();

        template.Update("  Updated Name  ", ValidItems);

        template.Name.Should().Be("Updated Name");
    }

    [Fact]
    public void Update_TrimsItems()
    {
        var template = CreateValidTemplate();
        var items = new List<string> { "  Trimmed  " };

        template.Update("Name", items);

        template.Items.Should().ContainSingle().Which.Should().Be("Trimmed");
    }

    [Fact]
    public void Update_FiltersEmptyItems()
    {
        var template = CreateValidTemplate();
        var items = new List<string> { "Keep", "", "  ", "Also keep" };

        template.Update("Name", items);

        template.Items.Should().HaveCount(2);
        template.Items.Should().ContainInOrder("Keep", "Also keep");
    }

    [Fact]
    public void Update_PreservesCreatedAtUtc()
    {
        var template = CreateValidTemplate();
        var originalCreatedAt = template.CreatedAtUtc;

        template.Update("New Name", ["New Item"]);

        template.CreatedAtUtc.Should().Be(originalCreatedAt);
    }

    [Fact]
    public void Update_PreservesUserId()
    {
        var template = CreateValidTemplate();
        var originalUserId = template.UserId;

        template.Update("New Name", ["New Item"]);

        template.UserId.Should().Be(originalUserId);
    }
}
