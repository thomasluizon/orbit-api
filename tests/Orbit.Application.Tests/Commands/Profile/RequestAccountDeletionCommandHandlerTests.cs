using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Jobs;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Profile;

public class RequestAccountDeletionCommandHandlerTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IBackgroundJobClient _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
    private readonly RequestAccountDeletionCommandHandler _handler;
    private Job? _enqueuedJob;

    private static readonly Guid UserId = Guid.NewGuid();

    public RequestAccountDeletionCommandHandlerTests()
    {
        _backgroundJobClient.Create(Arg.Do<Job>(job => _enqueuedJob = job), Arg.Any<IState>());
        _handler = new RequestAccountDeletionCommandHandler(_cache, _userRepo, _backgroundJobClient);
    }

    [Fact]
    public async Task Handle_UserFound_CachesCodeAndEnqueuesEmailWithCachedCode()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new RequestAccountDeletionCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _cache.TryGetValue($"delete:{user.Email}", out VerificationEntry? entry).Should().BeTrue();
        AssertEnqueuedDeletionEmail(user.Email, entry!.Code, "en");
    }

    [Fact]
    public async Task Handle_NonDefaultLanguageUser_EnqueuesWithThatLanguage()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetLanguage("pt-BR");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new RequestAccountDeletionCommand(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _cache.TryGetValue($"delete:{user.Email}", out VerificationEntry? entry).Should().BeTrue();
        AssertEnqueuedDeletionEmail(user.Email, entry!.Code, "pt-BR");
    }

    [Fact]
    public async Task Handle_WithinCooldownWindow_ReturnsFailure_AndDoesNotEnqueue()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _cache.Set($"delete:{user.Email}", new VerificationEntry("111111", 0, DateTime.UtcNow),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

        var result = await _handler.Handle(new RequestAccountDeletionCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        _backgroundJobClient.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure_AndDoesNotEnqueue()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new RequestAccountDeletionCommand(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("User not found.");
        _backgroundJobClient.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    private void AssertEnqueuedDeletionEmail(string expectedEmail, string expectedCode, string expectedLanguage)
    {
        _backgroundJobClient.Received(1).Create(
            Arg.Any<Job>(), Arg.Is<IState>(state => state is EnqueuedState));
        _enqueuedJob.Should().NotBeNull();
        _enqueuedJob!.Type.Should().Be<SendAccountDeletionCodeEmailJob>();
        _enqueuedJob.Method.Name.Should().Be(nameof(SendAccountDeletionCodeEmailJob.ExecuteAsync));
        _enqueuedJob.Args.Should().Equal(expectedEmail, expectedCode, expectedLanguage);
    }
}
