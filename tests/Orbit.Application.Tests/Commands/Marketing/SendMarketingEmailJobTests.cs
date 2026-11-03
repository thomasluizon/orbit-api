using FluentAssertions;
using NSubstitute;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Entities;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Commands.Marketing;

public class SendMarketingEmailJobTests
{
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    [Fact]
    public async Task ExecuteAsync_SendsMarketingEmailWithGivenArguments()
    {
        var job = new SendMarketingEmailJob(_emailService, Substitute.For<IGenericRepository<MarketingDelivery>>(), Substitute.For<IUnitOfWork>(), Substitute.For<IMarketingUnsubscribeTokenService>(), Options.Create(new MarketingSettings()));

        await job.ExecuteAsync(
            "user@test.com", "Subject", "<p>Body</p>", "pt-BR", "https://api.useorbit.org/api/marketing/unsubscribe?token=abc");

        await _emailService.Received(1).SendMarketingEmailAsync(
            "user@test.com", "Subject", "<p>Body</p>", "pt-BR",
            "https://api.useorbit.org/api/marketing/unsubscribe?token=abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesSendFailure()
    {
        _emailService.SendMarketingEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("provider down")));

        var job = new SendMarketingEmailJob(_emailService, Substitute.For<IGenericRepository<MarketingDelivery>>(), Substitute.For<IUnitOfWork>(), Substitute.For<IMarketingUnsubscribeTokenService>(), Options.Create(new MarketingSettings()));

        var act = () => job.ExecuteAsync("user@test.com", "Subject", "<p>Body</p>", "en", "https://x/unsub");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
