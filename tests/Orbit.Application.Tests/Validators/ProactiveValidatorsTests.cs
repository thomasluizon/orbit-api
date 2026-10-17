using FluentAssertions;
using Orbit.Application.Proactive.Commands;
using Orbit.Application.Proactive.Queries;
using Orbit.Application.Proactive.Validators;

namespace Orbit.Application.Tests.Validators;

public sealed class ProactiveValidatorsTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Status_RequiresOwnerAndItem(bool ownerPresent, bool itemPresent, bool valid)
    {
        var query = new GetProactiveItemStatusQuery(ownerPresent ? Guid.NewGuid() : Guid.Empty,
            itemPresent ? Guid.NewGuid() : Guid.Empty);
        new GetProactiveItemStatusQueryValidator().Validate(query).IsValid.Should().Be(valid);
    }

    [Fact]
    public void Validators_RequireOwnerAndItem()
    {
        new GetCurrentProactiveQueryValidator().Validate(new GetCurrentProactiveQuery(Guid.Empty)).IsValid.Should().BeFalse();
        new GetCurrentProactiveQueryValidator().Validate(new GetCurrentProactiveQuery(Guid.NewGuid())).IsValid.Should().BeTrue();
        var validator = new RecordProactiveOutcomeCommandValidator();
        validator.Validate(new RecordProactiveOutcomeCommand(Guid.Empty, Guid.NewGuid(), false)).IsValid.Should().BeFalse();
        validator.Validate(new RecordProactiveOutcomeCommand(Guid.NewGuid(), Guid.Empty, true)).IsValid.Should().BeFalse();
        validator.Validate(new RecordProactiveOutcomeCommand(Guid.NewGuid(), Guid.NewGuid(), false)).IsValid.Should().BeTrue();
    }
}
