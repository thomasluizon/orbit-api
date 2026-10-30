using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OAuthTokenCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_RequestAbortsAfterKeyWrite_CommitsUsableAccessAndRefreshTokens(bool refresh)
    {
        using var caller = new CancellationTokenSource();
        var disconnect = new DisconnectAfterKeyWrite(caller);
        using var factory = new SqliteOrbitDbContextFactory(disconnect);
        var db = factory.Context;
        var user = User.Create("OAuth", "oauth@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var store = new OAuthAuthorizationStore(new OAuthStateStore(db, TimeProvider.System), TimeProvider.System);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var controller = new OAuthController(Substitute.For<IMediator>(), store, new GenericRepository<ApiKey>(db), work,
            Options.Create(new GoogleSettings()), new ConfigurationBuilder().Build(), NullLogger<OAuthController>.Instance,
            Substitute.For<IOAuthClientMetadataService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = caller.Token } }
        };
        const string verifier = "test-pkce-verifier";
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var code = store.CreateCode(user.Id, challenge, "https://claude.ai/callback", "client");
        string? refreshToken = null;
        if (refresh)
        {
            var initial = (OkObjectResult)await controller.Token("authorization_code", code, verifier,
                "https://claude.ai/callback", default, client_id: "client");
            refreshToken = JsonSerializer.SerializeToElement(initial.Value).GetProperty("refresh_token").GetString();
        }
        disconnect.Enabled = true;

        var response = await controller.Token(refresh ? "refresh_token" : "authorization_code", code, verifier,
            "https://claude.ai/callback", controller.HttpContext.RequestAborted, refreshToken, "client");

        caller.IsCancellationRequested.Should().BeTrue();
        var body = JsonSerializer.SerializeToElement(response.Should().BeOfType<OkObjectResult>().Subject.Value);
        db.Database.CurrentTransaction.Should().BeNull();
        db.ChangeTracker.Clear();
        var activeKey = await db.ApiKeys.SingleAsync(key => !key.IsRevoked);
        activeKey.Verify(body.GetProperty("access_token").GetString()!).Should().BeTrue();
        store.ReadRefreshToken(body.GetProperty("refresh_token").GetString()!)!.ApiKeyId.Should().Be(activeKey.Id);
    }

    private sealed class DisconnectAfterKeyWrite(CancellationTokenSource caller) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context?.ChangeTracker.Entries<ApiKey>().Any() == true)
                caller.Cancel();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}
