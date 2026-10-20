using FluentAssertions;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Validators;

public class UpdateChecklistTemplateValidatorTests
{
    [Fact]
    public void Validate_RequiresOwnershipIdsAndAtLeastOneChange()
    {
        var validator = new UpdateChecklistTemplateCommandValidator();
        validator.Validate(new UpdateChecklistTemplateCommand(Guid.Empty, Guid.NewGuid(), "New")).IsValid.Should().BeFalse();
        validator.Validate(new UpdateChecklistTemplateCommand(Guid.NewGuid(), Guid.Empty, "New")).IsValid.Should().BeFalse();
        validator.Validate(new UpdateChecklistTemplateCommand(Guid.NewGuid(), Guid.NewGuid())).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_RejectsBlankNamesAndItems(string blank)
    {
        var validator = new UpdateChecklistTemplateCommandValidator();
        validator.Validate(new UpdateChecklistTemplateCommand(Guid.NewGuid(), Guid.NewGuid(), blank)).IsValid.Should().BeFalse();
        validator.Validate(new UpdateChecklistTemplateCommand(Guid.NewGuid(), Guid.NewGuid(), Items: [blank])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_UsesCreateLimitsForSuppliedFields()
    {
        var validator = new UpdateChecklistTemplateCommandValidator();
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        validator.Validate(new UpdateChecklistTemplateCommand(userId, id, new string('a', 101))).IsValid.Should().BeFalse();
        validator.Validate(new UpdateChecklistTemplateCommand(userId, id, Items: [])).IsValid.Should().BeFalse();
        validator.Validate(new UpdateChecklistTemplateCommand(userId, id, Items: Enumerable.Repeat("Item", AppConstants.MaxChecklistItems + 1).ToArray())).IsValid.Should().BeFalse();
        validator.Validate(new UpdateChecklistTemplateCommand(userId, id, Items: [new string('a', AppConstants.MaxChecklistItemTextLength + 1)])).IsValid.Should().BeFalse();
        validator.Validate(new UpdateChecklistTemplateCommand(userId, id, new string('a', 100))).IsValid.Should().BeTrue();
        validator.Validate(new UpdateChecklistTemplateCommand(userId, id, Items: Enumerable.Repeat(new string('a', AppConstants.MaxChecklistItemTextLength), AppConstants.MaxChecklistItems).ToArray())).IsValid.Should().BeTrue();
    }
}
