using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Services.Notion;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ResetProviderRemovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_RevokesProvidersBeforeDeletingLocalData_AndToleratesProviderFailure(bool fails)
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var user = await test.Db.Users.SingleAsync();
        user.SetGoogleTokens("access", "stored-refresh");
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        test.Api.RevokeAsync("access-token", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            (await test.Db.NotionConnections.AnyAsync()).Should().BeTrue();
            if (fails) throw new HttpRequestException("Notion unavailable");
        });
        var requests = 0;
        using var http = new HttpClient(new CallbackHandler(async (request, ct) =>
        {
            requests++;
            request.RequestUri!.ToString().Should().Be("https://oauth2.googleapis.com/revoke");
            (await request.Content!.ReadAsStringAsync(ct)).Should().Be("token=stored-refresh");
            (await test.Db.Users.SingleAsync()).GoogleRefreshToken.Should().Be("stored-refresh");
            return new HttpResponseMessage(fails ? HttpStatusCode.BadGateway : HttpStatusCode.OK);
        }));
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(GoogleTokenService.HttpClientName).Returns(http);
        var logger = Substitute.For<ILogger<GoogleTokenService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var google = new GoogleTokenService(clients, new ConfigurationBuilder().Build(), logger);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new ResetAccountCommandHandler(new GenericRepository<User>(test.Db),
            new AccountResetRepository(test.Db, Substitute.For<Orbit.Domain.Events.IAccountEventCollector>()),
            test.UnitOfWork, test.Dates, cache, test.Connections, google);
        (await handler.Handle(new(test.User.Id), default)).IsSuccess.Should().BeTrue();
        await test.Api.Received(1).RevokeAsync("access-token", Arg.Any<CancellationToken>());
        requests.Should().Be(1);
        (await test.Db.NotionConnections.AnyAsync()).Should().BeFalse();
        (await test.Db.Users.SingleAsync()).GoogleRefreshToken.Should().BeNull();
        if (fails)
        {
            logger.Received(1).Log(LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(),
                Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>());
            test.Logger.Received(1).Log(LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(),
                Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>());
        }
    }

    [Fact]
    public async Task Reset_WithoutProviderConnections_MakesNoProviderCall()
    {
        using var test = new NotionTestContext();
        var google = Substitute.For<IGoogleTokenService>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new ResetAccountCommandHandler(new GenericRepository<User>(test.Db),
            new AccountResetRepository(test.Db, Substitute.For<Orbit.Domain.Events.IAccountEventCollector>()),
            test.UnitOfWork, test.Dates, cache, test.Connections, google);
        (await handler.Handle(new(test.User.Id), default)).IsSuccess.Should().BeTrue();
        await test.Api.DidNotReceive().RevokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await google.DidNotReceive().RevokeBestEffortAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleRevoke_CancelledCaller_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new CallbackHandler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(GoogleTokenService.HttpClientName).Returns(http);
        var google = new GoogleTokenService(clients, new ConfigurationBuilder().Build(), Substitute.For<ILogger<GoogleTokenService>>());
        var revoke = () => google.RevokeBestEffortAsync(Guid.NewGuid(), "refresh", cancellation.Token);
        await revoke.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
