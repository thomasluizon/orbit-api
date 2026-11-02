using NSubstitute;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests;

internal static class ModerationTestDouble
{
    public static IContentModerationService Clean()
    {
        var service = Substitute.For<IContentModerationService>();
        service.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        service.CheckTextAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => service.CheckTextAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        return service;
    }
}
