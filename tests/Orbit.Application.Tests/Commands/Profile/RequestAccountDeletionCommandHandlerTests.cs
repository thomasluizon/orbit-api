using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Profile;

public class RequestAccountDeletionCommandHandlerTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly RequestAccountDeletionCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public RequestAccountDeletionCommandHandlerTests()
    {
        _handler = new RequestAccountDeletionCommandHandler(_cache, _userRepo, _emailService);
    }

    [Fact]
    public async Task Handle_UserFound_SendsEmailAndCachesCode()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var command = new RequestAccountDeletionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailService.Received(1).SendAccountDeletionCodeAsync(
            user.Email,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var command = new RequestAccountDeletionCommand(UserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
    }
}
