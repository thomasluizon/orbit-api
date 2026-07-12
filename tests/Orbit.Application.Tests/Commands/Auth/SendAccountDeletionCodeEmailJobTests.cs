using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Jobs;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class SendAccountDeletionCodeEmailJobTests
{
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    [Fact]
    public async Task ExecuteAsync_SendsDeletionCodeWithGivenArguments()
    {
        var job = new SendAccountDeletionCodeEmailJob(_emailService);

        await job.ExecuteAsync("user@test.com", "123456", "pt-BR");

        await _emailService.Received(1).SendAccountDeletionCodeAsync(
            "user@test.com", "123456", "pt-BR", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesSendFailure()
    {
        _emailService.SendAccountDeletionCodeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("provider down")));

        var job = new SendAccountDeletionCodeEmailJob(_emailService);

        var act = () => job.ExecuteAsync("user@test.com", "123456", "en");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
