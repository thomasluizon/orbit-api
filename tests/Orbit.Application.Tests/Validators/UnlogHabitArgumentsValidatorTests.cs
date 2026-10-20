using FluentAssertions;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Validators;

public sealed class UnlogHabitArgumentsValidatorTests
{
    private readonly UnlogHabitArgumentsValidator _validator = new();
    [Fact]
    public void ValidIdentifiers_AreAccepted() =>
        _validator.Validate(new UnlogHabitArguments(Guid.NewGuid(), Guid.NewGuid())).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyIdentifiers_AreRejected(bool emptyUser)
    {
        var result = _validator.Validate(new UnlogHabitArguments(emptyUser ? Guid.Empty : Guid.NewGuid(),
            emptyUser ? Guid.NewGuid() : Guid.Empty));
        result.IsValid.Should().BeFalse();
    }
}
