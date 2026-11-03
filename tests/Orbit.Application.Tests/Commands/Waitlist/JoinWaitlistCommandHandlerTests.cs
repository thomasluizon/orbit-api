using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Waitlist.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Waitlist;

public class JoinWaitlistCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IWaitlistConfirmationTokenService _tokenService = Substitute.For<IWaitlistConfirmationTokenService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly JoinWaitlistCommandHandler _handler;
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly List<Job> _queued = [];

    public JoinWaitlistCommandHandlerTests()
    {
        _tokenService.CreateToken(Arg.Any<string>(), Arg.Any<string>()).Returns("tok.sig");
        var settings = Options.Create(new WaitlistSettings
        {
            SigningKey = "k",
            ApiBaseUrl = "https://api.useorbit.org"
        });
        _jobs.Create(Arg.Do<Job>(job => _queued.Add(job)), Arg.Any<IState>());
        _handler = new JoinWaitlistCommandHandler(ChallengeTestDatabase.For(_cache).Store(), _tokenService, _jobs, settings);
    }

    [Fact]
    public async Task Handle_NewEmail_MintsTokenAndSendsConfirmationEmail()
    {
        var result = await _handler.Handle(new JoinWaitlistCommand("User@Test.com", "en"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _tokenService.Received(1).CreateToken("user@test.com", "en");
        await DeliverAsync();
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "user@test.com",
            "https://api.useorbit.org/api/waitlist/confirm?token=tok.sig",
            "en",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("EN", "en")]
    [InlineData("pt-br", "pt-BR")]
    public async Task Handle_AcceptedMixedCaseLanguage_SignsCanonicalLanguage(string input, string expected)
    {
        var result = await _handler.Handle(new JoinWaitlistCommand("mixed@test.com", input), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _tokenService.Received(1).CreateToken("mixed@test.com", expected);
        await DeliverAsync();
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "mixed@test.com", Arg.Any<string>(), expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithinCooldown_DoesNotSendSecondEmail()
    {
        await _handler.Handle(new JoinWaitlistCommand("user@test.com"), CancellationToken.None);
        var second = await _handler.Handle(new JoinWaitlistCommand("user@test.com"), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        await DeliverAsync();
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DifferentEmails_EachReceivesEmail()
    {
        await _handler.Handle(new JoinWaitlistCommand("a@test.com"), CancellationToken.None);
        await _handler.Handle(new JoinWaitlistCommand("b@test.com"), CancellationToken.None);

        await DeliverAsync();
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "a@test.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "b@test.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Handle_BlockedEmailProvider_ReturnsBeforeDelivery()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _emailService.SendWaitlistConfirmationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(blocked.Task);
        var request = _handler.Handle(new JoinWaitlistCommand("queued@test.com"), default);
        var completed = await Task.WhenAny(request, Task.Delay(TimeSpan.FromMilliseconds(500)));
        blocked.SetResult();
        await request;
        completed.Should().BeSameAs(request);
        _queued.Should().ContainSingle();
        await DeliverAsync();
    }

    private async Task DeliverAsync()
    {
        await _emailService.DidNotReceive().SendWaitlistConfirmationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        foreach (var job in _queued)
        {
            job.Type.Should().Be<SendWaitlistConfirmationJob>();
            await new SendWaitlistConfirmationJob(_emailService).ExecuteAsync((string)job.Args[0], (string)job.Args[1], (string)job.Args[2], default);
        }
    }

}
