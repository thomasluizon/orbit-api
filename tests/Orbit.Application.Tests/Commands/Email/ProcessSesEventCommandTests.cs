using FluentAssertions;
using NSubstitute;
using Orbit.Application.Email.Commands;
using Orbit.Application.Email.Validators;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Email;

public sealed class ProcessSesEventCommandTests
{
    [Fact]
    public async Task HandlerPassesRawPayloadToVerifier()
    {
        var processor = Substitute.For<ISesEventProcessor>();
        processor.ProcessAsync("signed payload", Arg.Any<CancellationToken>()).Returns(true);
        var result = await new ProcessSesEventCommandHandler(processor)
            .Handle(new ProcessSesEventCommand("signed payload"), CancellationToken.None);
        result.Should().BeTrue();
        await processor.Received(1).ProcessAsync("signed payload", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ValidatorRejectsEmptyOrOversizedPayload()
    {
        var validator = new ProcessSesEventCommandValidator();
        validator.Validate(new ProcessSesEventCommand("")).IsValid.Should().BeFalse();
        validator.Validate(new ProcessSesEventCommand(new string('x', 262145))).IsValid.Should().BeFalse();
        validator.Validate(new ProcessSesEventCommand("{}")).IsValid.Should().BeTrue();
    }
}
