using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.AI;

public class AiDailyBudgetTests
{
    private static readonly DateOnly Today = new(2026, 1, 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedUser_IsRefusedBeforeModel_WhileOtherUserProceeds(bool tokensExhausted)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var first = User.Create("First", "first@example.com").Value;
        var second = User.Create("Second", "second@example.com").Value;
        first.StartTrial(DateTime.UtcNow.AddDays(-1));
        second.StartTrial(DateTime.UtcNow.AddDays(-1));
        database.Context.Users.AddRange(first, second);
        var row = AiBudgetCall.Reserve(first.Id, Today, tokensExhausted ? AppConfigKeys.DefaultFreeAiTokensPerDay : 1);
        row.Settle(tokensExhausted ? AppConfigKeys.DefaultFreeAiTokensPerDay : 1);
        database.Context.AiBudgetCalls.Add(row);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => tokensExhausted ? call.ArgAt<int>(1) : 1);
        config.GetAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<long>(1));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        await database.Context.SaveChangesAsync();
        using var provider = new ServiceCollection()
            .AddScoped(_ => database.CreateContext())
            .AddScoped<IUnitOfWork>(scope => new UnitOfWork(scope.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()))
            .AddSingleton(config).AddSingleton(dates).BuildServiceProvider();
        var budget = new AiDailyBudget(provider.GetRequiredService<IServiceScopeFactory>());
        var raw = new AiCompletionClient(Microsoft.Extensions.Options.Options.Create(new AiSettings { ApiKey = "test-key" }),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        var client = new BudgetedAiCompletionClient(raw, budget, new HttpContextAccessor(), NullLogger<BudgetedAiCompletionClient>.Instance);
        var calls = 0;
        var recorder = new BudgetAwareAiUsageRecorder(new AiUsageRecorder(provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new AiSettings()), NullLogger<AiUsageRecorder>.Instance), new HttpContextAccessor());
        async Task<int> Model(AiCompletionClient _)
        {
            calls++;
            await recorder.RecordAsync("chat", "model", 0, 20, 3, 23);
            return 42;
        }
        Func<Task> exhausted = async () => await client.RunAsync(first.Id, Model, default, 1_000);
        await exhausted.Should().ThrowAsync<AiBudgetExceededException>();
        calls.Should().Be(0);
        var summary = new Orbit.Infrastructure.Services.AiSummaryService(client,
            NullLogger<Orbit.Infrastructure.Services.AiSummaryService>.Instance);
        for (var i = 0; i < 4; i++)
        {
            var context = new DailySummaryContext(Today.AddDays(-i), Today.AddDays(i), Today,
                i % 2 == 0 ? "en" : "pt-BR", null, 0, 0, new Dictionary<Guid, DateOnly>(), UserId: first.Id);
            var result = await summary.GenerateSummaryAsync([], context);
            result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        }
        (await client.RunAsync(second.Id, Model, default, 1_000)).Should().Be(42);
        calls.Should().Be(1);
        using var read = database.CreateContext();
        read.AiBudgetCalls.Single(call => call.UserId == second.Id).ChargedTokens.Should().Be(23);
        read.AiUsageDaily.Single().UserId.Should().Be(second.Id);
        read.AiUsageDaily.Single().TotalTokens.Should().Be(23);
    }

    [Theory]
    [InlineData(false, 40_000L, 0.02, false)]
    [InlineData(true, 240_000L, 0.12, false)]
    [InlineData(false, 40_000L, 0.02, true)]
    [InlineData(true, 240_000L, 0.12, true)]
    public async Task DailySpendCeiling_RefusesNextCompletionWithExistingQuotaResponse(bool pro, long tokenCeiling, decimal dollars, bool oversizedConfig)
    {
        (pro ? AppConfigKeys.DefaultProAiTokensPerDay : AppConfigKeys.DefaultFreeAiTokensPerDay)
            .Should().Be(tokenCeiling);
        (tokenCeiling * 0.50m / 1_000_000).Should().Be(dollars);
        using var database = new SqliteOrbitDbContextFactory();
        var user = User.Create("Budget", "budget@example.com").Value;
        if (pro)
            user.StartTrial(DateTime.UtcNow.AddDays(1));
        else
            user.StartTrial(DateTime.UtcNow.AddDays(-1));
        database.Context.Users.Add(user);
        var usage = AiBudgetCall.Reserve(user.Id, Today, tokenCeiling - 1_000);
        usage.Settle(tokenCeiling - 1_000);
        database.Context.AiBudgetCalls.Add(usage);
        await database.Context.SaveChangesAsync();
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        config.GetAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(call => oversizedConfig ? long.MaxValue : call.ArgAt<long>(1));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        using var provider = new ServiceCollection()
            .AddScoped(_ => database.CreateContext())
            .AddScoped<IUnitOfWork>(scope => new UnitOfWork(scope.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()))
            .AddSingleton(config).AddSingleton(dates).BuildServiceProvider();
        var budget = new AiDailyBudget(provider.GetRequiredService<IServiceScopeFactory>());
        var raw = new AiCompletionClient(Microsoft.Extensions.Options.Options.Create(new AiSettings { ApiKey = "test-key" }),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        var client = new BudgetedAiCompletionClient(raw, budget, new HttpContextAccessor(), NullLogger<BudgetedAiCompletionClient>.Instance);
        var summary = new Orbit.Infrastructure.Services.AiSummaryService(client,
            NullLogger<Orbit.Infrastructure.Services.AiSummaryService>.Instance);

        var calls = 0;
        await client.RunAsync(user.Id, _ =>
        {
            calls++;
            AiCallContext.Current!.Report(1_000);
            return Task.FromResult(42);
        }, default, 1_000);
        var response = await summary.GenerateSummaryAsync([], new DailySummaryContext(Today, Today, Today,
            "en", null, 0, 0, new Dictionary<Guid, DateOnly>(), UserId: user.Id));

        response.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        response.Error.Should().Be(AiBudgetExceededException.Error.Message);
        using var read = database.CreateContext();
        read.AiBudgetCalls.Count().Should().Be(2);
        read.AiBudgetCalls.Sum(call => call.ChargedTokens).Should().Be(tokenCeiling);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Gateway_AlwaysChargesFailure_AndRequiresUser()
    {
        var budget = Substitute.For<IAiDailyBudget>();
        var userId = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        budget.ReserveAsync(userId, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(reservation);
        var raw = new AiCompletionClient(Microsoft.Extensions.Options.Options.Create(new AiSettings { ApiKey = "test-key" }),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        var client = new BudgetedAiCompletionClient(raw, budget, new HttpContextAccessor(), NullLogger<BudgetedAiCompletionClient>.Instance);
        Func<Task> failed = () => client.RunAsync<int>(userId, _ => throw new InvalidOperationException("provider"), default);
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await budget.Received(1).SettleAsync(reservation, null, Arg.Any<CancellationToken>());
        Func<Task> anonymous = () => client.RunAsync<int>(null, _ => Task.FromResult(1), default);
        await anonymous.Should().ThrowAsync<InvalidOperationException>().WithMessage("*authenticated user*");
    }
}
