using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Jobs;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class SendVerificationCodeEmailJobTests
{
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    [Fact]
    public async Task ExecuteAsync_SendsVerificationCodeWithGivenArguments()
    {
        var job = new SendVerificationCodeEmailJob(_emailService);

        await job.ExecuteAsync("user@test.com", "123456", "pt-BR");

        await _emailService.Received(1).SendVerificationCodeAsync(
            "user@test.com", "123456", "pt-BR", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesSendFailure()
    {
        _emailService.SendVerificationCodeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("provider down")));

        var job = new SendVerificationCodeEmailJob(_emailService);

        var act = () => job.ExecuteAsync("user@test.com", "123456", "en");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
