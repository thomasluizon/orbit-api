using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GooglePlayPushTokenValidatorTests
{
    private static GooglePlayPushTokenValidator CreateValidator() =>
        new(Options.Create(new GooglePlaySettings
        {
            RtdnAudience = "https://api.useorbit.test/play/rtdn",
            RtdnServiceAccountEmail = "rtdn@orbit-test.iam.gserviceaccount.com",
        }));

    [Fact]
    public async Task IsValidAsync_MissingBearerPrefix_ReturnsFalse()
    {
        var result = await CreateValidator().IsValidAsync("not-a-bearer-token");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsValidAsync_EmptyHeader_ReturnsFalse()
    {
        var result = await CreateValidator().IsValidAsync(string.Empty);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsValidAsync_MalformedBearerToken_ReturnsFalse()
    {
        var result = await CreateValidator().IsValidAsync("Bearer not-a-real-jwt");

        result.Should().BeFalse();
    }
}
