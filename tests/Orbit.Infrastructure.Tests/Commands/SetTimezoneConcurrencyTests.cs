using FluentAssertions;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Api.Middleware;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Commands;

public class SetTimezoneConcurrencyTests
{
    [Fact]
    public async Task SetTimezone_TransientConflict_RetriesThroughMediatorAndInvalidatesOnce()
    {
        await using var database = new UserRowConcurrencyHarness(_ => { }, maxRaces: 0);
        var user = User.Create("Timezone user", "timezone@example.com").Value;
        await database.SeedAsync(user);
        database.TransientUserConflicts = 1;
        var dates = Substitute.For<IUserDateService>();
        using var provider = CreateProvider(database, dates);

        var result = await provider.GetRequiredService<IMediator>()
            .Send(new SetTimezoneCommand(user.Id, "America/Sao_Paulo"));

        result.IsSuccess.Should().BeTrue();
        database.SaveAttempts.Should().Be(2);
        database.Work.Received(1).ResetTracking();
        await dates.Received(1).InvalidateUserDatePreferencesAsync(user.Id, Arg.Any<CancellationToken>());
        await using var verify = database.CreateContext();
        (await verify.Users.SingleAsync()).TimeZone.Should().Be("America/Sao_Paulo");
    }

    [Fact]
    public async Task SetTimezone_PersistentConflict_ReturnsTypedHttp409AfterThreeAttempts()
    {
        await using var database = new UserRowConcurrencyHarness(_ => { }, maxRaces: 0);
        var user = User.Create("Timezone user", "timezone@example.com").Value;
        await database.SeedAsync(user);
        database.ForceUserConflict = true;
        var dates = Substitute.For<IUserDateService>();
        using var provider = CreateProvider(database, dates);
        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            provider.GetRequiredService<IMediator>().Send(new SetTimezoneCommand(user.Id, "America/Sao_Paulo")));
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var mapper = new ConcurrencyExceptionHandler(NullLogger<ConcurrencyExceptionHandler>.Instance);

        (await mapper.TryHandleAsync(http, exception, default)).Should().BeTrue();

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ConcurrentUpdateConflict);
        database.SaveAttempts.Should().Be(3);
        database.Work.Received(2).ResetTracking();
        await dates.DidNotReceive().InvalidateUserDatePreferencesAsync(user.Id, Arg.Any<CancellationToken>());
        await using var verify = database.CreateContext();
        (await verify.Users.SingleAsync()).TimeZone.Should().BeNull();
    }

    [Fact]
    public async Task SetTimezone_ConcurrentProfileWrites_ReloadsUnderLockWithoutExhaustingRetries()
    {
        await using var database = new UserRowConcurrencyHarness(user => user.SetName("Concurrent name"));
        var user = User.Create("Timezone user", "timezone@example.com").Value;
        await database.SeedAsync(user);
        var dates = Substitute.For<IUserDateService>();
        using var provider = CreateProvider(database, dates);

        var result = await provider.GetRequiredService<IMediator>()
            .Send(new SetTimezoneCommand(user.Id, "America/Sao_Paulo"));

        result.IsSuccess.Should().BeTrue();
        database.SaveAttempts.Should().Be(1);
        database.ConcurrentWrites.Should().Be(1);
        await using var verify = database.CreateContext();
        var saved = await verify.Users.SingleAsync();
        saved.Name.Should().Be("Concurrent name");
        saved.TimeZone.Should().Be("America/Sao_Paulo");
        await dates.Received(1).InvalidateUserDatePreferencesAsync(user.Id, Arg.Any<CancellationToken>());
    }

    private static ServiceProvider CreateProvider(UserRowConcurrencyHarness database, IUserDateService dates) =>
        new ServiceCollection()
            .AddLogging()
            .AddMediatR(configuration =>
            {
                configuration.RegisterServicesFromAssemblyContaining<SetTimezoneCommand>();
                configuration.AddOpenBehavior(typeof(ConcurrencyRetryBehavior<,>));
            })
            .AddSingleton<IGenericRepository<User>>(new GenericRepository<User>(database.Context))
            .AddSingleton(database.Work)
            .AddSingleton(dates)
            .BuildServiceProvider();
}
