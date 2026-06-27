using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class SocialAccessGuardTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly SocialAccessGuard _guard;

    public SocialAccessGuardTests()
    {
        _guard = new SocialAccessGuard(_userRepository);
    }

    [Fact]
    public async Task EnsureEnabled_OptedIn_ReturnsTrackedUser()
    {
        var user = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, user);

        var result = await _guard.EnsureEnabledAsync(user.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(user);
    }

    [Fact]
    public async Task EnsureEnabled_OptedOut_ReturnsSocialDisabled()
    {
        var user = SocialTestHelpers.OptedOutUser();
        SocialTestHelpers.StubUsers(_userRepository, user);

        var result = await _guard.EnsureEnabledAsync(user.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
    }

    [Fact]
    public async Task EnsureEnabled_UnknownUser_ReturnsUserNotFound()
    {
        SocialTestHelpers.StubUsers(_userRepository);

        var result = await _guard.EnsureEnabledAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }
}
