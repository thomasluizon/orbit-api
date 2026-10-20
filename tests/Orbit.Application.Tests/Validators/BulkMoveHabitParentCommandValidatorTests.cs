using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class BulkMoveHabitParentCommandValidatorTests
{
    private readonly BulkMoveHabitParentCommandValidator _validator = new();

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    public void BatchSize_UsesSharedLimit(int count, bool valid)
    {
        var command = new BulkMoveHabitParentCommand(Guid.NewGuid(),
            Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList(), null);

        _validator.Validate(command).IsValid.Should().Be(valid);
    }

    [Fact]
    public void OverLimit_UsesMoveValidationCode()
    {
        var command = new BulkMoveHabitParentCommand(Guid.NewGuid(),
            Enumerable.Range(0, AppConstants.MaxBulkOperationSize + 1).Select(_ => Guid.NewGuid()).ToList(), null);

        _validator.Validate(command).Errors.Should().Contain(e => e.ErrorCode == ValidationErrorCodes.BulkMoveParentLimit);
    }

    [Fact]
    public void NullHabitIds_AreRejectedWithoutThrowing()
    {
        var command = new BulkMoveHabitParentCommand(Guid.NewGuid(), null!, null);

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void EmptyIdsAndParent_AreRejected()
    {
        var command = new BulkMoveHabitParentCommand(Guid.Empty, [Guid.Empty], Guid.Empty);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.PropertyName).Should().Contain("UserId").And.Contain("ParentId");
        result.Errors.Should().HaveCount(3);
    }
}
