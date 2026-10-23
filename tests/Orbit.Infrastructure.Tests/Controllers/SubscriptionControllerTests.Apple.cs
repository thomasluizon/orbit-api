using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class SubscriptionControllerTests
{
    [Fact]
    public async Task AppStoreVerify_Disabled_ReturnsServiceUnavailable()
    {
        _mediator.Send(Arg.Any<VerifyAppStorePurchaseCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AppStoreVerifyResponse>(ErrorMessages.PaymentServiceUnavailable));
        ((ObjectResult)await _controller.VerifyAppStorePurchase(new("signed"), default)).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
}
