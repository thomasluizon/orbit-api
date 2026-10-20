using NSubstitute;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests;

internal static class ModerationTestDouble
{
    public static IContentModerationService Clean()
    {
        var service = Substitute.For<IContentModerationService>();
        service.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        return service;
    }
}
