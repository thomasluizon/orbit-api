using FluentAssertions;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class CompleteSignupReferralTests
{
    [Fact]
    public async Task CouponFailure_RollsBackReferralAndAllowsRetryWithoutRepeatingOtherSignupJobs()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var (referrer, user) = await SeedSignupAsync(database.Context);
        var rewards = Substitute.For<IReferralRewardService>();
        var analytics = Substitute.For<IProductAnalytics>();
        var email = Substitute.For<IEmailService>();
        var attempts = 0;
        rewards.CreateReferralCouponAsync(user.Id, $"orbit-referral-signup-{user.Id}", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++attempts != 1)
                    return Task.FromResult("referral-coupon");

                database.Context.Users.Local.Single(account => account.Id == user.Id)
                    .ReferredByUserId.Should().Be(referrer.Id);
                database.Context.Referrals.Local.Should().ContainSingle();
                return Task.FromException<string>(new InvalidOperationException("Coupon unavailable"));
            });
        using var services = CreateServices(database.Context, rewards, analytics, email);
        var job = services.GetRequiredService<CompleteSignupJob>();

        var exception = await Record.ExceptionAsync(() => job.ProcessReferralAsync(user.Id, "friend"));

        await using (var verification = database.CreateContext())
        {
            var persisted = await verification.Users.SingleAsync(account => account.Id == user.Id);
            persisted.ReferredByUserId.Should().BeNull();
            persisted.ReferralCouponId.Should().BeNull();
            persisted.SignupReferralCompletedAtUtc.Should().BeNull();
            persisted.SignupCompletedAtUtc.Should().BeNull();
            (await verification.Referrals.CountAsync()).Should().Be(0);
        }
        exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("Coupon unavailable");
        database.Context.ChangeTracker.Entries().Should().BeEmpty();

        await job.CaptureSignupAsync(user.Id);
        await services.GetRequiredService<SendWelcomeEmailJob>().ExecuteAsync(user.Id, "en");
        await using (var verification = database.CreateContext())
        {
            var persisted = await verification.Users.SingleAsync(account => account.Id == user.Id);
            persisted.SignupAnalyticsCompletedAtUtc.Should().NotBeNull();
            persisted.SignupWelcomeCompletedAtUtc.Should().NotBeNull();
            persisted.SignupReferralCompletedAtUtc.Should().BeNull();
            persisted.SignupCompletedAtUtc.Should().BeNull();
            (await verification.Referrals.CountAsync()).Should().Be(0);
        }

        await using var retryContext = database.CreateContext();
        using var retryServices = CreateServices(retryContext, rewards, analytics, email);
        var retry = retryServices.GetRequiredService<CompleteSignupJob>();
        await retry.ProcessReferralAsync(user.Id, "replacement");
        await retry.ProcessReferralAsync(user.Id, "replacement");
        await retry.CaptureSignupAsync(user.Id);
        await retryServices.GetRequiredService<SendWelcomeEmailJob>().ExecuteAsync(user.Id, "en");

        await using var completed = database.CreateContext();
        var completedUser = await completed.Users.SingleAsync(account => account.Id == user.Id);
        completedUser.ReferredByUserId.Should().Be(referrer.Id);
        completedUser.ReferralCouponId.Should().Be("referral-coupon");
        completedUser.SignupReferralCompletedAtUtc.Should().NotBeNull();
        completedUser.SignupCompletedAtUtc.Should().NotBeNull();
        var referral = (await completed.Referrals.ToListAsync()).Should().ContainSingle().Subject;
        referral.ReferrerId.Should().Be(referrer.Id);
        referral.ReferredUserId.Should().Be(user.Id);
        attempts.Should().Be(2);
        analytics.Received(1).CaptureUserEvent(user.Id, "signup_completed", "Free");
        await email.Received(1).SendWelcomeEmailAsync(user.Email, user.Name, "pt-BR", CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalReferralFailure_RecordsCompletionAndReplaySkipsReferral(bool alreadyReferred)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var (referrer, user) = await SeedSignupAsync(database.Context, alreadyReferred ? "friend" : "invalid");
        if (alreadyReferred)
        {
            var tracked = await database.Context.Users.SingleAsync(account => account.Id == user.Id);
            tracked.SetReferredBy(referrer.Id);
            tracked.SetReferralCoupon("existing-coupon");
            database.Context.Referrals.Add(Referral.Create(referrer.Id, user.Id));
            await database.Context.SaveChangesAsync();
            database.Context.ChangeTracker.Clear();
        }
        var rewards = Substitute.For<IReferralRewardService>();
        using var services = CreateServices(database.Context, rewards,
            Substitute.For<IProductAnalytics>(), Substitute.For<IEmailService>());
        var result = await services.GetRequiredService<IMediator>().Send(
            new ProcessReferralCodeCommand(user.Id, alreadyReferred ? "friend" : "invalid"));
        result.ErrorCode.Should().Be(alreadyReferred ? ErrorMessages.AlreadyReferred.Code : ErrorMessages.InvalidReferralCode.Code);

        await services.GetRequiredService<CompleteSignupJob>().ProcessReferralAsync(user.Id, "friend");

        await using var verification = database.CreateContext();
        var persisted = await verification.Users.SingleAsync(account => account.Id == user.Id);
        persisted.SignupReferralCompletedAtUtc.Should().NotBeNull();
        persisted.ReferredByUserId.Should().Be(alreadyReferred ? referrer.Id : null);
        persisted.ReferralCouponId.Should().Be(alreadyReferred ? "existing-coupon" : null);
        (await verification.Referrals.CountAsync()).Should().Be(alreadyReferred ? 1 : 0);
        var checkpoint = persisted.SignupReferralCompletedAtUtc;

        using var replayServices = CreateServices(verification, rewards,
            Substitute.For<IProductAnalytics>(), Substitute.For<IEmailService>());
        await replayServices.GetRequiredService<CompleteSignupJob>().ProcessReferralAsync(user.Id, "friend");
        await verification.Entry(persisted).ReloadAsync();
        persisted.SignupReferralCompletedAtUtc.Should().Be(checkpoint);
        await rewards.DidNotReceiveWithAnyArgs().CreateReferralCouponAsync(default);
    }

    private static async Task<(User Referrer, User NewUser)> SeedSignupAsync(OrbitDbContext context, string code = "friend")
    {
        var referrer = User.Create("Referrer", "referrer@example.com").Value;
        referrer.SetReferralCode("friend");
        var user = User.Create("Person", "person@example.com").Value;
        user.BeginSignup("pt-BR", code);
        context.Users.AddRange(referrer, user);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (referrer, user);
    }

    private static ServiceProvider CreateServices(OrbitDbContext context, IReferralRewardService rewards,
        IProductAnalytics analytics, IEmailService email)
    {
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.MaxReferrals, AppConstants.DefaultMaxReferrals, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultMaxReferrals);
        return new ServiceCollection().AddLogging()
            .AddMediatR(configuration => configuration.RegisterServicesFromAssemblyContaining<ProcessReferralCodeCommand>())
            .AddSingleton<IGenericRepository<User>>(new GenericRepository<User>(context))
            .AddSingleton<IGenericRepository<Referral>>(new GenericRepository<Referral>(context))
            .AddSingleton<IUnitOfWork>(new UnitOfWork(context, new DatabaseConnectionSettings()))
            .AddSingleton(config)
            .AddSingleton(rewards)
            .AddSingleton(analytics)
            .AddSingleton(email)
            .AddSingleton(Substitute.For<IBackgroundJobClient>())
            .AddSingleton<CompleteSignupJob>()
            .AddSingleton<SendWelcomeEmailJob>()
            .BuildServiceProvider();
    }
}
