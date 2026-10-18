using System.Linq.Expressions;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class AuthRegistrationWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_RegistrationDoesNotChangeCollaboratorOrder(bool google)
    {
        var returning = await SignInAsync(google, true);
        var signup = await SignInAsync(google, false);

        signup.Should().Equal(returning);
    }

    private static async Task<List<string>> SignInAsync(bool google, bool exists)
    {
        var calls = new List<string>();
        var users = Substitute.For<IGenericRepository<User>>();
        var work = Substitute.For<IUnitOfWork>();
        var sessions = Substitute.For<IAuthSessionService>();
        var analytics = Substitute.For<IProductAnalytics>();
        var email = Substitute.For<IEmailService>();
        users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("lookup");
                return exists ? User.Create("Existing", "person@example.com").Value : null;
            });
        users.AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(_ => { calls.Add("insert"); return Task.CompletedTask; });
        work.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { calls.Add("save"); return Task.FromResult(1); });
        sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("session");
                return Result.Success(new SessionTokens("access", "refresh"));
            });
        analytics.When(a => a.CaptureUserEvent(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>()))
            .Do(_ => calls.Add("analytics"));

        if (google)
        {
            var flow = new GoogleSignInFlow(users, work, sessions, email,
                Substitute.For<IServiceScopeFactory>(), analytics, NullLogger<GoogleSignInFlow>.Instance);
            (await flow.CompleteAsync("person@example.com", "Person", "en", null, null, null,
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }
        else
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            cache.Set("verify:person@example.com", new VerificationEntry("123456", 0, DateTime.UtcNow));
            var handler = new VerifyCodeCommandHandler(cache, users, work, sessions, email,
                Substitute.For<IMediator>(), analytics, NullLogger<VerifyCodeCommandHandler>.Instance);
            (await handler.Handle(new VerifyCodeCommand("person@example.com", "123456"),
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        return calls;
    }
}
