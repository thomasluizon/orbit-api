using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AuthSessionServiceTests
{
    private readonly IGenericRepository<UserSession> _userSessionRepository = Substitute.For<IGenericRepository<UserSession>>();
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly AuthSessionService _sut;

    public AuthSessionServiceTests()
    {
        _tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>()).Returns("access-token");

        _sut = new AuthSessionService(
            _userSessionRepository,
            _userRepository,
            _tokenService,
            _unitOfWork,
            Options.Create(new JwtSettings
            {
                SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
                Issuer = "test-issuer",
                Audience = "test-audience",
                ExpiryMinutes = 0,
                RefreshExpiryDays = 90
            }));
    }

    [Fact]
    public async Task CreateSessionAsync_AddsPersistedSessionAndReturnsTokens()
    {
        var userId = Guid.NewGuid();

        var result = await _sut.CreateSessionAsync(userId, "thomas@test.com", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access-token");
        result.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        await _userSessionRepository.Received(1).AddAsync(Arg.Any<UserSession>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshSessionAsync_RotatesExistingSession()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var existingToken = "refresh-token";
        var session = UserSession.Create(user.Id, Hash(existingToken), DateTime.UtcNow.AddDays(7)).Value;

        _userSessionRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserSession, bool>>>(),
            Arg.Any<Func<IQueryable<UserSession>, IQueryable<UserSession>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(session);
        _userRepository.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.RefreshSessionAsync(existingToken, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RefreshToken.Should().NotBe(existingToken);
        session.TokenHash.Should().NotBe(Hash(existingToken));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshSessionAsync_ExpiredSession_ReturnsFailure()
    {
        var userId = Guid.NewGuid();
        var session = UserSession.Create(userId, Hash("expired-token"), DateTime.UtcNow.AddDays(-1)).Value;

        _userSessionRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserSession, bool>>>(),
            Arg.Any<Func<IQueryable<UserSession>, IQueryable<UserSession>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(session);

        var result = await _sut.RefreshSessionAsync("expired-token", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_SESSION");
    }

    [Fact]
    public async Task RefreshSessionAsync_UserNotFound_ReturnsFailure()
    {
        var userId = Guid.NewGuid();
        var existingToken = "refresh-token";
        var session = UserSession.Create(userId, Hash(existingToken), DateTime.UtcNow.AddDays(7)).Value;

        _userSessionRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserSession, bool>>>(),
            Arg.Any<Func<IQueryable<UserSession>, IQueryable<UserSession>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(session);
        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.RefreshSessionAsync(existingToken, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_SESSION");
    }

    [Fact]
    public async Task RevokeSessionAsync_MarksSessionRevoked()
    {
        var session = UserSession.Create(Guid.NewGuid(), Hash("refresh-token"), DateTime.UtcNow.AddDays(7)).Value;

        _userSessionRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserSession, bool>>>(),
            Arg.Any<Func<IQueryable<UserSession>, IQueryable<UserSession>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(session);

        var result = await _sut.RevokeSessionAsync("refresh-token", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        session.RevokedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessionAsync_MissingSession_ReturnsFailure()
    {
        _userSessionRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserSession, bool>>>(),
            Arg.Any<Func<IQueryable<UserSession>, IQueryable<UserSession>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((UserSession?)null);

        var result = await _sut.RevokeSessionAsync("missing-token", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_SESSION");
    }

    private static string Hash(string token)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }
}
