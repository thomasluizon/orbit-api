using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Services;

public class AuthSessionServiceTests
{
    private readonly IGenericRepository<UserSession> _sessionRepository = Substitute.For<IGenericRepository<UserSession>>();
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly AuthSessionService _service;

    private readonly User _user = User.Create("Thomas", "thomas@example.com").Value;
    private UserSession? _storedSession;

    public AuthSessionServiceTests()
    {
        _service = new AuthSessionService(
            _sessionRepository,
            _userRepository,
            _tokenService,
            _unitOfWork,
            Options.Create(new JwtSettings
            {
                SecretKey = "OrbitDevelopmentSecretKey123456789012345678901234567890",
                Issuer = "OrbitApi",
                Audience = "OrbitClient",
                ExpiryHours = 168,
                ExpiryMinutes = 0,
                RefreshExpiryDays = null,
            }));

        _sessionRepository
            .AddAsync(Arg.Any<UserSession>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                _storedSession = callInfo.Arg<UserSession>();
                return Task.CompletedTask;
            });

        _sessionRepository
            .FindOneTrackedAsync(
                Arg.Any<Expression<Func<UserSession, bool>>>(),
                Arg.Any<Func<IQueryable<UserSession>, IQueryable<UserSession>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (_storedSession is null)
                    return null;

                var predicate = callInfo.Arg<Expression<Func<UserSession, bool>>>().Compile();
                return predicate(_storedSession) ? _storedSession : null;
            });

        _userRepository
            .GetByIdAsync(_user.Id, Arg.Any<CancellationToken>())
            .Returns(_user);

        _tokenService
            .GenerateToken(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns("access-token-1", "access-token-2", "access-token-3");
    }

    [Fact]
    public async Task CreateSessionAsync_WithNullRefreshExpiry_CreatesNonExpiringSession()
    {
        var result = await _service.CreateSessionAsync(_user.Id, _user.Email, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-token-1");
        result.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        _storedSession.Should().NotBeNull();
        _storedSession!.ExpiresAtUtc.Should().BeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshSessionAsync_WithNonExpiringSession_RotatesRefreshTokenWithoutAddingExpiry()
    {
        var createResult = await _service.CreateSessionAsync(_user.Id, _user.Email, CancellationToken.None);
        var originalTokenHash = _storedSession!.TokenHash;
        var originalLastUsedAtUtc = _storedSession.LastUsedAtUtc;

        await Task.Delay(5);

        var refreshResult = await _service.RefreshSessionAsync(createResult.Value.RefreshToken, CancellationToken.None);

        refreshResult.IsSuccess.Should().BeTrue();
        refreshResult.Value.AccessToken.Should().Be("access-token-2");
        refreshResult.Value.RefreshToken.Should().NotBe(createResult.Value.RefreshToken);
        _storedSession.ExpiresAtUtc.Should().BeNull();
        _storedSession.TokenHash.Should().NotBe(originalTokenHash);
        _storedSession.LastUsedAtUtc.Should().BeAfter(originalLastUsedAtUtc);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessionAsync_RevokesStoredSession()
    {
        var createResult = await _service.CreateSessionAsync(_user.Id, _user.Email, CancellationToken.None);

        var revokeResult = await _service.RevokeSessionAsync(createResult.Value.RefreshToken, CancellationToken.None);

        revokeResult.IsSuccess.Should().BeTrue();
        _storedSession!.RevokedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshSessionAsync_WithRevokedSession_ReturnsFailure()
    {
        var createResult = await _service.CreateSessionAsync(_user.Id, _user.Email, CancellationToken.None);
        _storedSession!.Revoke(DateTime.UtcNow);

        var refreshResult = await _service.RefreshSessionAsync(createResult.Value.RefreshToken, CancellationToken.None);

        refreshResult.IsFailure.Should().BeTrue();
        refreshResult.Error.Should().Be(ErrorMessages.InvalidSession);
        refreshResult.ErrorCode.Should().Be(ErrorCodes.InvalidSession);
    }

    [Fact]
    public void UserSession_CanUse_AllowsNullExpiryUntilRevoked()
    {
        var session = UserSession.Create(_user.Id, "token-hash", null).Value;

        session.CanUse(DateTime.UtcNow.AddYears(10)).Should().BeTrue();

        session.Revoke(DateTime.UtcNow);

        session.CanUse(DateTime.UtcNow.AddYears(10)).Should().BeFalse();
    }
}
