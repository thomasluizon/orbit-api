using Orbit.Infrastructure.Tests.Persistence;
using Orbit.Infrastructure.Persistence;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using System.Linq.Expressions;
using Orbit.Application.Auth.Jobs;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Controllers;

[Collection("ProcessEnvironment")]
public class OAuthControllerTests : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _oauthDb = new();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly MutableTimeProvider _timeProvider = new();
    private readonly OAuthAuthorizationStore _authStore;
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<OAuthController> _logger = Substitute.For<ILogger<OAuthController>>();
    private readonly OAuthController _controller;
    private readonly IOAuthClientMetadataService _clientMetadata = Substitute.For<IOAuthClientMetadataService>();

    private static readonly Guid UserId = Guid.NewGuid();

    public OAuthControllerTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<IActionResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<IActionResult>>>()(call.Arg<CancellationToken>()));
        _unitOfWork.ExecuteReplayableInTransactionAsync(Arg.Any<Func<CancellationToken, Task<IActionResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<IActionResult>>>()(call.Arg<CancellationToken>()));
        _authStore = new OAuthAuthorizationStore(new OAuthStateStore(_oauthDb.Context, _timeProvider), _timeProvider);
        var googleSettings = Options.Create(new GoogleSettings
        {
            ClientId = "test-google-client-id",
            AllowedRedirectUris = ["https://api.useorbit.org/oauth/google/callback"]
        });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth:AllowedRedirectHosts:0"] = "claude.ai",
                ["OAuth:AllowedRedirectHosts:1"] = "claude.com"
            })
            .Build();

        _controller = new OAuthController(
            _mediator, _authStore, _apiKeyRepo, _unitOfWork, googleSettings, config, _logger, _clientMetadata);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.useorbit.org");
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        _oauthDb.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OAuthAccessToken_IsBounded_AndRefreshRotatesPreservingScopes()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client", scopes: [AgentScopes.ReadHabits], isReadOnly: true);
        ApiKey? minted = null;
        await _apiKeyRepo.AddAsync(Arg.Do<ApiKey>(key => minted = key), Arg.Any<CancellationToken>());
        var response = (OkObjectResult)await _controller.Token("authorization_code", code, verifier, "https://claude.ai/callback", default, client_id: "client");
        minted.Should().NotBeNull();
        minted!.ExpiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromSeconds(5));
        var old = minted;
        var payload = JsonSerializer.SerializeToElement(response.Value);
        payload.GetProperty("expires_in").GetInt32().Should().Be(3600);
        var refresh = payload.GetProperty("refresh_token").GetString()!;
        _apiKeyRepo.GetByIdAsync(old.Id, Arg.Any<CancellationToken>()).Returns(old);
        _apiKeyRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<Func<IQueryable<ApiKey>, IQueryable<ApiKey>>?>(), Arg.Any<CancellationToken>()).Returns(old);
        var refreshed = await _controller.Token("refresh_token", null, null, null, default, refresh, client_id: "client");
        refreshed.Should().BeOfType<OkObjectResult>();
        minted!.Id.Should().NotBe(old.Id);
        minted.Scopes.Should().Equal(AgentScopes.ReadHabits);
        minted.IsReadOnly.Should().BeTrue();
        old.IsRevoked.Should().BeTrue();
        var refreshedPayload = JsonSerializer.SerializeToElement(((OkObjectResult)refreshed).Value);
        BCrypt.Net.BCrypt.Verify(refreshedPayload.GetProperty("access_token").GetString()!, minted.KeyHash).Should().BeTrue();
        (await _controller.Token("refresh_token", null, null, null, default, refresh, client_id: "client")).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GoogleRetry_PreservesReducedRequestedScopes()
    {
        _controller.HttpContext.Request.Host = new HostString("unconfigured.example.com");
        var response = (RedirectResult)(await _controller.GoogleStart("client", "https://claude.ai/callback", "state",
            "challenge", "S256", scope: AgentScopes.ReadHabits));
        ExtractQueryParam(response.Url, "scope").Should().Be(AgentScopes.ReadHabits);
    }

    [Theory]
    [InlineData("en", "View your routine and progress", "Create and edit your routine")]
    [InlineData("pt-BR", "Ver sua rotina e progresso", "Criar e editar sua rotina")]
    public void Consent_ListsRequestedScopes_AndPersistsReadOnlyChoice(string language, string readGroup, string writeGroup)
    {
        var (verifier, challenge) = GeneratePkce();
        var ticket = _authStore.CreatePendingConsent(new ConsentEntry(UserId, challenge, "https://claude.ai/callback", "client", "state", null, language, [AgentScopes.ReadHabits, AgentScopes.WriteHabits]));
        var page = (ContentResult)_controller.Consent(ticket);
        page.Content.Should().Contain(AgentScopes.ReadHabits).And.Contain(AgentScopes.WriteHabits).And.Contain("read_only");
        System.Net.WebUtility.HtmlDecode(page.Content).Should().Contain("<details>").And.Contain(readGroup).And.Contain(writeGroup);
        var grant = (RedirectResult)_controller.GrantConsent(ticket, "read_only");
        var code = ExtractQueryParam(grant.Url, "code");
        var entry = _authStore.ExchangeCode(code, verifier, "https://claude.ai/callback");
        entry!.Scopes.Should().Equal(AgentScopes.ReadHabits);
        entry.IsReadOnly.Should().BeTrue();
        _controller.GrantConsent(ticket, "requested").Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData("en", "https://claude.ai/callback")]
    [InlineData("pt-BR", "https://claude.ai/callback?existing=kept")]
    public void Consent_Cancel_ConsumesTicketWithoutIssuingCode_AndPreservesState(string language, string redirectUri)
    {
        const string clientState = "estado com espaços & + / ? = % ç";
        var ticket = _authStore.CreatePendingConsent(new ConsentEntry(UserId, "challenge", redirectUri,
            "client", clientState, null, language));
        _controller.Request.Headers.AcceptLanguage = language == "en" ? "pt-BR" : "en";

        var redirect = _controller.CancelConsent(ticket).Should().BeOfType<RedirectResult>().Subject;
        ExtractQueryParam(redirect.Url, "error").Should().Be("access_denied");
        ExtractQueryParam(redirect.Url, "state").Should().Be(clientState);
        redirect.Url.Should().NotContain("code=");
        if (redirectUri.Contains('?'))
            ExtractQueryParam(redirect.Url, "existing").Should().Be("kept");
        _authStore.ReadPendingConsent(ticket)!.IsConsumed.Should().BeTrue();
        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(0);

        foreach (var result in new[]
                 {
                     _controller.CancelConsent(ticket), _controller.GrantConsent(ticket, "requested"),
                     _controller.GrantConsent(ticket, "read_only"), _controller.Consent(ticket)
                 })
        {
            var page = result.Should().BeOfType<ContentResult>().Subject;
            page.StatusCode.Should().Be(200);
            page.Content.Should().Contain($"<html lang=\"{language}\">").And.NotContain("<form")
                .And.NotContain("code=");
        }
        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(0);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void Consent_RequestedGrant_PreservesAllScopesAndNonce(string language)
    {
        var (verifier, challenge) = GeneratePkce();
        var scopes = new[] { AgentScopes.ReadHabits, AgentScopes.WriteHabits, AgentScopes.DeleteHabits, AgentScopes.ChatInteract };
        var ticket = _authStore.CreatePendingConsent(new ConsentEntry(UserId, challenge,
            "https://claude.ai/callback?existing=kept", "client", "original state & +", "nonce", language, scopes));

        var redirect = _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>().Subject;
        ExtractQueryParam(redirect.Url, "state").Should().Be("original state & +");
        ExtractQueryParam(redirect.Url, "existing").Should().Be("kept");
        var entry = _authStore.ExchangeCode(ExtractQueryParam(redirect.Url, "code"), verifier,
            "https://claude.ai/callback?existing=kept");
        entry.Should().NotBeNull();
        entry!.Scopes.Should().Equal(scopes);
        entry.IsReadOnly.Should().BeFalse();
        entry.Nonce.Should().Be("nonce");
        _controller.CancelConsent(ticket).Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(200);
        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(0);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("cancel")]
    public void Consent_UnknownAction_DoesNotConsumeTicket(string access)
    {
        var ticket = _authStore.CreatePendingConsent(new ConsentEntry(UserId, "challenge",
            "https://claude.ai/callback", "client", "state", null, "en"));

        var response = _controller.GrantConsent(ticket, access).Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetString().Should().Be("invalid_request");
        _authStore.ReadPendingConsent(ticket)!.IsConsumed.Should().BeFalse();
        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(0);
    }

    [Theory]
    [InlineData("en", "Access allowed", "Return to Claude or your MCP client to continue.")]
    [InlineData("pt-BR", "Acesso permitido", "Volte para o Claude ou seu cliente MCP para continuar.")]
    public void Consent_RepeatSubmitAndGet_ShowCompletionWithoutCreatingAnotherCode(string language, string title, string body)
    {
        var (verifier, challenge) = GeneratePkce();
        var ticket = _authStore.CreatePendingConsent(new ConsentEntry(UserId, challenge,
            "https://claude.ai/callback", "client", "state", null, language));
        _controller.HttpContext.Request.Headers.AcceptLanguage = language == "en" ? "pt-BR" : "en";

        var grant = _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>().Subject;
        var code = ExtractQueryParam(grant.Url, "code");
        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(1);

        foreach (var result in new[] { _controller.GrantConsent(ticket, "read_only"), _controller.CancelConsent(ticket), _controller.Consent(ticket) })
        {
            var page = result.Should().BeOfType<ContentResult>().Subject;
            page.StatusCode.Should().Be(200);
            page.ContentType.Should().Be("text/html");
            page.Content.Should().Contain($"<html lang=\"{language}\">").And.Contain(title).And.Contain(body)
                .And.NotContain("<form").And.NotContain("code=").And.NotContain(code);
        }

        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(1);
        _authStore.ExchangeCode(code, verifier, "https://claude.ai/callback").Should().NotBeNull();
        _controller.GrantConsent(ticket, "requested").Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(200);
        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(0);
    }

    [Theory]
    [InlineData(false, false, "en")]
    [InlineData(true, false, "en")]
    [InlineData(true, true, "en")]
    [InlineData(false, false, "pt-BR")]
    [InlineData(true, false, "pt-BR")]
    [InlineData(true, true, "pt-BR")]
    public void Consent_UnknownOrExpiredTicket_KeepsInvalidRequestPage(bool expired, bool consumed, string language)
    {
        _controller.Request.Headers.AcceptLanguage = language;
        var ticket = expired
            ? _authStore.CreatePendingConsent(new ConsentEntry(UserId, "challenge", "https://claude.ai/callback",
                "client", "state", null, language))
            : "unknown";
        if (expired)
        {
            if (consumed)
                _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>();
            _timeProvider.Advance(TimeSpan.FromMinutes(5));
        }

        foreach (var result in new[] { _controller.Consent(ticket), _controller.GrantConsent(ticket, "requested"),
                     _controller.GrantConsent(ticket, "read_only"), _controller.CancelConsent(ticket) })
        {
            var page = result.Should().BeOfType<ContentResult>().Subject;
            page.StatusCode.Should().Be(400);
            page.Content.Should().Contain($"<html lang=\"{language}\">")
                .And.Contain(OAuthPageStrings.For(language).InvalidRequestTitle);
        }
    }

    [Theory]
    [InlineData("en", "https://untrusted.example/callback")]
    [InlineData("en", "http://claude.ai/callback")]
    [InlineData("pt-BR", "https://untrusted.example/callback")]
    [InlineData("pt-BR", "http://claude.ai/callback")]
    public void Consent_DisallowedRedirect_RejectsGrantAndCancel_EvenAfterConsumption(string language, string redirectUri)
    {
        _controller.Request.Headers.AcceptLanguage = language;
        foreach (var action in new Func<string, IActionResult>[]
                 {
                     ticket => _controller.GrantConsent(ticket, "requested"),
                     ticket => _controller.GrantConsent(ticket, "read_only"),
                     _controller.CancelConsent
                 })
        {
            var ticket = _authStore.CreatePendingConsent(new ConsentEntry(UserId, "challenge", redirectUri,
                "client", "state", null, language));
            foreach (var result in new[] { action(ticket), action(ticket) })
            {
                var page = result.Should().BeOfType<ContentResult>().Subject;
                page.StatusCode.Should().Be(400);
                page.Content.Should().Contain($"<html lang=\"{language}\">")
                    .And.Contain(OAuthPageStrings.For(language).InvalidRequestTitle);
            }
        }
        _oauthDb.Context.OAuthStates.AsNoTracking().Count(state => state.Kind == "code").Should().Be(0);
    }

    [Fact]
    public void Consent_FormGuardsPendingSubmissions_WithoutDisablingTheSubmitter()
    {
        var ticket = _authStore.CreatePendingConsent(new ConsentEntry(UserId, "challenge", "https://claude.ai/callback",
            "client", "state", null, "en"));
        var page = _controller.Consent(ticket).Should().BeOfType<ContentResult>().Subject;

        page.Content.Should().Contain("id=\"consent-form\"").And.Contain("<script>")
            .And.Contain("addEventListener('submit'").And.Contain("event.preventDefault()")
            .And.Contain("'aria-busy', 'true'").And.Contain("'aria-disabled', 'true'")
            .And.Contain("name=\"access\" value=\"read_only\"").And.Contain("name=\"access\" value=\"requested\"")
            .And.NotContain(".disabled = true");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GoogleCallback_Reactivation_RestoresOnlyUnexpiredStripeRenewal(bool expired, bool play)
    {
        var user = User.Create("Returning", "returning@example.com").Value;
        var expiryAtUtc = DateTime.UtcNow.AddDays(expired ? -1 : 3);
        if (play)
            user.SetPlaySubscription("play-token", expiryAtUtc);
        else
            user.SetStripeSubscription("sub_paid", expiryAtUtc);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>()(call.Arg<CancellationToken>()));
        var users = Substitute.For<IAuthUserStore>();
        users.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(user);
        var sessions = Substitute.For<IAuthSessionService>();
        sessions.CreateSessionAsync(user.Id, user.Email, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("access", "refresh")));
        var jobs = Substitute.For<IBackgroundJobClient>();
        Job? enqueued = null;
        jobs.Create(Arg.Do<Job>(job => { if (job.Type == typeof(CancelSubscriptionOnDeletionJob)) enqueued = job; }), Arg.Any<IState>());
        var flow = new GoogleSignInFlow(users, _unitOfWork, sessions,
            new SignupJobDispatcher(jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), jobs, new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>()));
        var exchange = Substitute.For<IGoogleAuthorizationCodeService>();
        exchange.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoogleCodeIdentity(user.Email, user.Name, "google-access", "google-refresh")));
        var googleHandler = new GoogleCodeAuthCommandHandler(exchange, _mediator);
        var completeHandler = new CompleteGoogleSignInCommandHandler(flow);
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => googleHandler.Handle(call.Arg<GoogleCodeAuthCommand>(), call.Arg<CancellationToken>()));
        _mediator.Send(Arg.Any<CompleteGoogleSignInCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => completeHandler.Handle(call.Arg<CompleteGoogleSignInCommand>(), call.Arg<CancellationToken>()));
        var pending = CreatePendingGoogleRequest();

        var result = await _controller.GoogleCallback(pending.GoogleState, "code", null, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>();
        user.IsDeactivated.Should().BeFalse();
        user.HasProAccess.Should().Be(!expired);
        user.Plan.Should().Be(expired ? UserPlan.Free : UserPlan.Pro);
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob)
            && (Guid)job.Args[0] == user.Id && !(bool)job.Args[1]), Arg.Any<IState>());
        jobs.Received(!expired && !play ? 2 : 1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        if (!expired && !play)
        {
            jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CancelSubscriptionOnDeletionJob)), Arg.Any<IState>());
            enqueued!.Type.Should().Be<CancelSubscriptionOnDeletionJob>();
            enqueued.Args.Should().Equal(user.Id, "sub_paid", null, false, CancellationToken.None);
        }
        else
            jobs.DidNotReceive().Create(Arg.Is<Job>(job => job.Type == typeof(CancelSubscriptionOnDeletionJob)), Arg.Any<IState>());
    }

    [Fact]
    public async Task GoogleCallback_RegistrationDoesNotChangeWorkAndUsesEmailProvisioningDefaults()
    {
        var traces = new List<string[]>();
        User? googleUser = null;
        foreach (var exists in new[] { true, false })
        {
            var calls = new List<string>();
            var users = Substitute.For<IAuthUserStore>();
            var work = Substitute.For<IUnitOfWork>();
            work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>()(call.Arg<CancellationToken>()));
            var sessions = Substitute.For<IAuthSessionService>();
            var jobs = Substitute.For<IBackgroundJobClient>();
            var exchange = Substitute.For<IGoogleAuthorizationCodeService>();
            exchange.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => { calls.Add("exchange"); return Result.Success(new GoogleCodeIdentity("person@example.com", "Person", "google-access", "google-refresh")); });
            users.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                calls.Add("upsert");
                googleUser = exists ? User.Create("Existing", "person@example.com").Value : call.Arg<User>();
                return googleUser;
            });
            work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ => { calls.Add("save"); return Task.FromResult(1); });
            sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => { calls.Add("session"); return Result.Success(new SessionTokens("access", "refresh")); });
            jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => { calls.Add("enqueue"); return "job"; });
            var flow = new GoogleSignInFlow(users, work, sessions, new SignupJobDispatcher(jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), jobs, new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>()));
            var completion = new CompleteGoogleSignInCommandHandler(flow);
            var codeHandler = new GoogleCodeAuthCommandHandler(exchange, _mediator);
            _mediator.Send(Arg.Any<CompleteGoogleSignInCommand>(), Arg.Any<CancellationToken>())
                .Returns(call => completion.Handle(call.Arg<CompleteGoogleSignInCommand>(), call.Arg<CancellationToken>()));
            _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
                .Returns(call => codeHandler.Handle(call.Arg<GoogleCodeAuthCommand>(), call.Arg<CancellationToken>()));
            var pending = CreatePendingGoogleRequest();

            var response = await _controller.GoogleCallback(pending.GoogleState, "code", null, CancellationToken.None);

            CompleteConsent(response.Should().BeOfType<RedirectResult>().Which.Url!).Should().StartWith("https://claude.ai/callback?code=");
            googleUser!.GoogleAccessToken.Should().BeNull();
            traces.Add(calls.ToArray());
        }

        traces[0].Should().Equal(traces[1]);
        traces[0].Should().Equal("exchange", "upsert", "save", "session", "enqueue");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("verify:email@example.com", new VerificationEntry("123456", 0, DateTime.UtcNow));
        var emailStore = Substitute.For<IAuthUserStore>();
        User? emailUser = null;
        emailStore.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => emailUser = call.Arg<User>());
        var emailSessions = Substitute.For<IAuthSessionService>();
        emailSessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("access", "refresh")));
        var emailWork = Substitute.For<IUnitOfWork>();
        emailWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<LoginResponse>>>>()(call.Arg<CancellationToken>()));
        var verify = new VerifyCodeCommandHandler(cache, emailStore, emailWork, emailSessions, new SignupJobDispatcher(Substitute.For<IBackgroundJobClient>(), new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), Substitute.For<IBackgroundJobClient>(), new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>()));

        (await verify.Handle(new VerifyCodeCommand("email@example.com", "123456"), CancellationToken.None)).IsSuccess.Should().BeTrue();

        googleUser!.Language.Should().Be(emailUser!.Language).And.Be("en");
    }

    [Fact]
    public void GetMetadata_ReturnsOkWithEndpoints()
    {
        var result = _controller.GetMetadata();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("authorization_endpoint");
        json.Should().Contain("token_endpoint");
        json.Should().Contain("registration_endpoint");
        JsonSerializer.SerializeToElement(ok.Value).GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void GetMetadata_UsesXForwardedProto_WhenPresent()
    {
        _controller.ControllerContext.HttpContext.Request.Headers["X-Forwarded-Proto"] = "https";
        _controller.ControllerContext.HttpContext.Request.Scheme = "http";

        var result = _controller.GetMetadata();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("https://");
    }

    [Fact]
    public void Register_WithClientName_Returns201WithClientId()
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"client_name":"My MCP","redirect_uris":["https://claude.ai/callback"]}""");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(201);
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("My MCP");
        json.Should().Contain("client_id");
    }

    [Fact]
    public void Register_WithoutClientName_DefaultsToMcpClient()
    {
        var body = JsonSerializer.Deserialize<JsonElement>("{}");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(201);
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("MCP Client");
    }

    [Fact]
    public void GetProtectedResourceMetadata_ReturnsOkWithMcpResource()
    {
        var result = _controller.GetProtectedResourceMetadata();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("/mcp");
        json.Should().Contain("bearer_methods_supported");
    }

    [Fact]
    public async Task GetProtectedResourceMetadata_BothRoutesReturnSameAnonymousDocument()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers().AddApplicationPart(typeof(OAuthController).Assembly)
            .AddControllersAsServices();
        builder.Services.AddSingleton(_controller);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseEndpoints(endpoints => endpoints.MapControllers());
        var pipeline = ((IApplicationBuilder)app).Build();
        var documents = new List<string>();

        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = "GET";
            context.Request.Path = path;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("api.useorbit.org");
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Response.Body = new MemoryStream();

            await pipeline(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.ContentType.Should().StartWith("application/json");
            context.Response.Body.Position = 0;
            documents.Add(await new StreamReader(context.Response.Body).ReadToEndAsync());
        }

        documents[1].Should().Be(documents[0]);
        using var document = JsonDocument.Parse(documents[0]);
        document.RootElement.GetProperty("resource").GetString().Should().Be("https://api.useorbit.org/mcp");
        document.RootElement.GetProperty("authorization_servers")[0].GetString().Should().Be("https://api.useorbit.org");
        document.RootElement.GetProperty("bearer_methods_supported")[0].GetString().Should().Be("header");
    }

    [Fact]
    public async Task Authorize_ValidParams_ReturnsHtmlContent()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
        content.Content.Should().Contain("/oauth/google/start?client_id=");
        content.Content.Should().NotContain("google.accounts.id");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("nonce-value", "cancelled")]
    public async Task Authorize_QueryBinding_PreservesOAuthNamesAndHtml(string? nonce, string? googleError)
    {
        var expected = OAuthLoginPage.Render(
            "client-123", "https://claude.ai/callback", "state-abc", "challenge-xyz", "S256", nonce,
            googleError is null ? null : "Google sign-in was cancelled. Try again or use email.", "en");
        var query = CreateAuthorizeQuery();
        if (nonce is not null) query["nonce"] = nonce;
        if (googleError is not null) query["google_error"] = googleError;

        var (context, result) = await BindAuthorizeQueryAsync(query);

        context.ModelState.IsValid.Should().BeTrue();
        var actual = result.Should().BeOfType<ContentResult>().Subject;
        actual.ContentType.Should().Be("text/html");
        actual.Content.Should().Be(expected);
    }

    [Theory]
    [InlineData("client_id")]
    [InlineData("redirect_uri")]
    [InlineData("response_type")]
    [InlineData("state")]
    [InlineData("code_challenge")]
    [InlineData("code_challenge_method")]
    public async Task Authorize_QueryBinding_MissingRequiredValue_ReturnsBadRequestWithQueryName(string missingKey)
    {
        var query = CreateAuthorizeQuery();
        query.Remove(missingKey);

        var (context, result) = await BindAuthorizeQueryAsync(query);

        context.ModelState.IsValid.Should().BeFalse();
        context.ModelState.Where(entry => entry.Value is { Errors.Count: > 0 })
            .Select(entry => entry.Key).Should().Equal(missingKey);
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value.Should().BeOfType<ValidationProblemDetails>().Which.Errors.Keys.Should().Equal(missingKey);
    }

    [Fact]
    public async Task Authorize_QueryBinding_MissingOptionalValues_ReturnsHtml()
    {
        var (context, result) = await BindAuthorizeQueryAsync(CreateAuthorizeQuery());

        context.ModelState.IsValid.Should().BeTrue();
        var content = result.Should().BeOfType<ContentResult>().Subject;
        (content.StatusCode ?? StatusCodes.Status200OK).Should().Be(StatusCodes.Status200OK);
        content.ContentType.Should().Be("text/html");
    }

    private static Dictionary<string, string?> CreateAuthorizeQuery() => new()
    {
        ["client_id"] = "client-123",
        ["redirect_uri"] = "https://claude.ai/callback",
        ["response_type"] = "code",
        ["state"] = "state-abc",
        ["code_challenge"] = "challenge-xyz",
        ["code_challenge_method"] = "S256"
    };

    private async Task<(ActionContext Context, IActionResult Result)> BindAuthorizeQueryAsync(
        Dictionary<string, string?> query)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(OAuthController).Assembly);
        using var provider = services.BuildServiceProvider();
        var action = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .Single(action => action.ControllerTypeInfo.AsType() == typeof(OAuthController)
                && action.ActionName == nameof(OAuthController.Authorize));
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Method = "GET";
        httpContext.Request.QueryString = QueryString.Create(query);
        var context = new ActionContext(httpContext, new RouteData(), action);
        var metadataProvider = (ModelMetadataProvider)provider.GetRequiredService<IModelMetadataProvider>();
        var binderFactory = provider.GetRequiredService<IModelBinderFactory>();
        var parameterBinder = provider.GetRequiredService<ParameterBinder>();
        var values = new QueryStringValueProvider(BindingSource.Query, httpContext.Request.Query,
            System.Globalization.CultureInfo.InvariantCulture);
        var arguments = new List<object?>();
        foreach (var parameter in action.Parameters.Cast<ControllerParameterDescriptor>())
        {
            var metadata = metadataProvider.GetMetadataForParameter(parameter.ParameterInfo);
            var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                BindingInfo = parameter.BindingInfo,
                CacheToken = parameter
            });
            var bound = await parameterBinder.BindModelAsync(context, binder, values, parameter, metadata, null);
            arguments.Add(bound.IsModelSet ? bound.Model : null);
        }

        var filter = new ModelStateInvalidFilter(provider.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value,
            NullLogger.Instance);
        var filters = new List<IFilterMetadata> { filter };
        var execution = new ActionExecutingContext(context, filters,
            action.Parameters.Select((parameter, index) => (parameter.Name, Value: arguments[index]))
                .ToDictionary(argument => argument.Name, argument => argument.Value), _controller);
        filter.OnActionExecuting(execution);
        if (execution.Result is not null)
            return (context, execution.Result);

        var result = await action.MethodInfo.Invoke(_controller, arguments.ToArray()).Should().BeAssignableTo<Task<IActionResult>>().Subject;
        return (context, result);
    }

    [Fact]
    public async Task GoogleStart_RedirectsToGoogleWithFreshStateAndItsOwnPkce()
    {
        var url = (await StartGoogle(nonce: "mcp-nonce")).Should().BeOfType<RedirectResult>().Subject.Url!;

        url.Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth?");
        ExtractQueryParam(url, "client_id").Should().Be("test-google-client-id");
        ExtractQueryParam(url, "redirect_uri").Should().Be("https://api.useorbit.org/oauth/google/callback");
        ExtractQueryParam(url, "response_type").Should().Be("code");
        ExtractQueryParam(url, "scope").Should().Be("openid email profile");
        ExtractQueryParam(url, "code_challenge_method").Should().Be("S256");
        ExtractQueryParam(url, "prompt").Should().Be("select_account");
        ExtractQueryParam(url, "state").Should().NotBe("client-state");
        url.Should().NotContain("mcp-challenge");

        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "jwt", "Alex", "alex@example.com")));
        await _controller.GoogleCallback(
            ExtractQueryParam(url, "state"), "google-code", null, CancellationToken.None);

        await _mediator.Received(1).Send(Arg.Is<GoogleCodeAuthCommand>(command =>
            Challenge(command.CodeVerifier) == ExtractQueryParam(url, "code_challenge")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleStart_AllocatesExactlyOneSingleUseRequest()
    {
        var url = (await StartGoogle()).Should().BeOfType<RedirectResult>().Subject.Url!;
        var googleState = ExtractQueryParam(url, "state");

        _authStore.ConsumeGoogleRequest(googleState).Should().NotBeNull();
        _authStore.ConsumeGoogleRequest(googleState).Should().BeNull();
    }

    [Theory]
    [InlineData("", "https://claude.ai/callback", "client-state", "mcp-challenge", "S256")]
    [InlineData("client-123", "https://claude.ai/callback", "", "mcp-challenge", "S256")]
    [InlineData("client-123", "https://claude.ai/callback", "client-state", "", "S256")]
    [InlineData("client-123", "https://claude.ai/callback", "client-state", "mcp-challenge", "plain")]
    [InlineData("client-123", "https://evil.example/callback", "client-state", "mcp-challenge", "S256")]
    [InlineData("client-123", "http://claude.ai/callback", "client-state", "mcp-challenge", "S256")]
    public async Task GoogleStart_RevalidatesTheRequestBeforeAllocating(
        string clientId, string redirectUri, string clientState, string challenge, string method)
    {
        (await _controller.GoogleStart(clientId, redirectUri, clientState, challenge, method))
            .Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GoogleStart_CapReachedReturnsUnavailableAndKeepsEmailSignIn()
    {
        for (var i = 0; i < OAuthAuthorizationStore.MaxPendingGoogleRequests; i++)
        {
            _authStore.TryCreateGoogleRequest("client", "https://claude.ai/callback", "client-state",
                "mcp-challenge", null, "https://api.useorbit.org/oauth/google/callback", "en")
                .Should().NotBeNull();
        }

        var redirect = (await StartGoogle()).Should().BeOfType<RedirectResult>().Subject;

        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("unavailable");
        var page = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "client-state",
            CodeChallenge = "mcp-challenge",
            CodeChallengeMethod = "S256",
            GoogleError = "unavailable",
        }))
            .Should().BeOfType<ContentResult>().Subject;
        page.Content.Should().Contain("Google sign-in is unavailable");
        page.Content.Should().Contain("/oauth/send-code");
    }

    [Fact]
    public async Task GoogleCallback_RejectsUnknownAndReusedState()
    {
        var state = CreatePendingGoogleRequest().GoogleState;
        (await _controller.GoogleCallback("forged-state", "code", null, CancellationToken.None))
            .Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);

        (await _controller.GoogleCallback(state, null, "access_denied", CancellationToken.None))
            .Should().BeOfType<RedirectResult>();
        (await _controller.GoogleCallback(state, "code", null, CancellationToken.None))
            .Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task GoogleCallback_RejectsExpiredState(string language)
    {
        var state = CreatePendingGoogleRequest().GoogleState;
        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        _controller.Request.Headers.AcceptLanguage = language;

        var page = (await _controller.GoogleCallback(state, "google-code", null, CancellationToken.None))
            .Should().BeOfType<ContentResult>().Subject;
        page.StatusCode.Should().Be(400);
        page.ContentType.Should().Be("text/html");
        page.Content.Should().Contain($"<html lang=\"{language}\">");
        page.Content.Should().Contain("<main class=\"card\">");
        page.Content.Should().Contain(OAuthPageStyles.Css);
        page.Content.Should().Contain(language == "pt-BR"
            ? "Volte ao seu cliente MCP e tente novamente."
            : "Return to your MCP client and try again.");
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleCallback_SuccessIssuesMcpCodeAcceptedForOriginalVerifierAndRedirectUri()
    {
        var (verifier, challenge) = GeneratePkce();
        var pending = CreatePendingGoogleRequest(challenge, "client-state&code=forged", "mcp-nonce");
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "jwt", "Alex", "alex@example.com")));

        var result = await _controller.GoogleCallback(
            pending.GoogleState, "google-code", null, CancellationToken.None);

        var url = CompleteConsent(result.Should().BeOfType<RedirectResult>().Subject.Url!);
        url.Should().StartWith("https://claude.ai/callback?code=");
        ExtractQueryParam(url, "state").Should().Be("client-state&code=forged");
        url.Should().NotContain("&code=forged");
        await _mediator.Received(1).Send(Arg.Is<GoogleCodeAuthCommand>(command =>
            command.Code == "google-code"
            && command.CodeVerifier == pending.GoogleCodeVerifier
            && command.RedirectUri == pending.GoogleRedirectUri
            && !command.PersistGoogleTokens), Arg.Any<CancellationToken>());

        var token = await _controller.Token("authorization_code", ExtractQueryParam(url, "code"),
            verifier, "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");
        token.Should().BeOfType<OkObjectResult>();
        JsonSerializer.Serialize(((OkObjectResult)token).Value).Should().Contain("mcp-nonce");
        (await _controller.Token("authorization_code", ExtractQueryParam(url, "code"),
            verifier, "https://claude.ai/callback", CancellationToken.None, client_id: "client-123"))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GoogleCallback_CancelledConsentReturnsLocalizedErrorWithEmailPath()
    {
        _controller.HttpContext.Request.Headers.AcceptLanguage = "pt-BR,pt;q=0.9";
        var state = CreatePendingGoogleRequest().GoogleState;

        var redirect = (await _controller.GoogleCallback(state, null, "access_denied", CancellationToken.None))
            .Should().BeOfType<RedirectResult>().Subject;
        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("cancelled");
        var page = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "client-state",
            CodeChallenge = "mcp-challenge",
            CodeChallengeMethod = "S256",
            GoogleError = "cancelled",
        }))
            .Should().BeOfType<ContentResult>().Subject;
        ReadRenderedError(page.Content!).Should().StartWith("O acesso com o Google foi cancelado");
        page.Content.Should().Contain("/oauth/send-code");
        page.Content.Should().Contain("if (request.error) setAlert(request.error)");
    }

    [Fact]
    public async Task GoogleCallback_ExchangeFailureReturnsAuthorizePageWithError()
    {
        var state = CreatePendingGoogleRequest().GoogleState;
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>(ErrorMessages.GoogleCodeExchangeFailed));

        var redirect = (await _controller.GoogleCallback(state, "bad-code", null, CancellationToken.None))
            .Should().BeOfType<RedirectResult>().Subject;

        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("failed");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("test-google-client-id", false)]
    public async Task GoogleStart_MissingConfigurationReturnsUnavailable(string clientId, bool callbackAllowed)
    {
        var settings = Options.Create(new GoogleSettings
        {
            ClientId = clientId,
            AllowedRedirectUris = callbackAllowed ? ["https://api.useorbit.org/oauth/google/callback"] : []
        });
        var controller = new OAuthController(_mediator, _authStore, _apiKeyRepo,
            _unitOfWork, settings, new ConfigurationBuilder().Build(), _logger, _clientMetadata)
        {
            ControllerContext = _controller.ControllerContext
        };

        var redirect = (await controller.GoogleStart("client-123", "https://claude.ai/callback", "client-state",
            "mcp-challenge", "S256", "preserved-nonce")).Should().BeOfType<RedirectResult>().Subject;
        ExtractQueryParam(redirect.Url!, "google_error").Should().Be("unavailable");
        ExtractQueryParam(redirect.Url!, "nonce").Should().Be("preserved-nonce");
    }

    [Theory]
    [InlineData("en", "failed", "Google sign-in failed")]
    [InlineData("pt-BR", "failed", "Não foi possível entrar com o Google")]
    [InlineData("en", "unavailable", "Google sign-in is unavailable")]
    [InlineData("pt-BR", "unavailable", "O acesso com o Google está indisponível")]
    public async Task Authorize_GoogleErrorRendersLocalizedMessageAndEmailPath(
        string language, string error, string message)
    {
        _controller.HttpContext.Request.Headers.AcceptLanguage = language;

        var page = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "client-state",
            CodeChallenge = "mcp-challenge",
            CodeChallengeMethod = "S256",
            GoogleError = error,
        }))
            .Should().BeOfType<ContentResult>().Subject;

        ReadRenderedError(page.Content!).Should().StartWith(message);
        page.Content.Should().Contain("/oauth/send-code");
    }

    [Fact]
    public async Task GoogleCallback_RejectsStoredRedirectUriThatIsNoLongerAllowed()
    {
        var state = _authStore.TryCreateGoogleRequest("client-123", "https://evil.example/callback",
            "client-state", "mcp-challenge", null,
            "https://api.useorbit.org/oauth/google/callback", "en")!.GoogleState;
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "jwt", "Alex", "alex@example.com")));

        var result = await _controller.GoogleCallback(state, "google-code", null, CancellationToken.None);

        result.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GoogleAuth_OneTapRoute_IsGoneSoAPostTo_OauthGoogle_Returns404()
    {
        var templates = typeof(OAuthController).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), false)
                .Cast<HttpMethodAttribute>())
            .Select(attribute => attribute.Template)
            .ToList();

        templates.Should().NotContain("/oauth/google");
        templates.Should().Contain("/oauth/google/start").And.Contain("/oauth/google/callback");
    }

    [Fact]
    public async Task Authorize_UnsupportedResponseType_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "token",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("unsupported_response_type");
    }

    [Fact]
    public async Task Authorize_MissingCodeChallenge_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "",
            CodeChallengeMethod = "S256",
        }));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Authorize_WrongCodeChallengeMethod_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "plain",
        }));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Authorize_DisallowedRedirectHost_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://evil.com/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Authorize_InvalidUri_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "not-a-valid-uri",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SendCode_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new OAuthController.SendCodeRequest("test@example.com");
        var result = await _controller.SendCode(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(true, null, 400)]
    [InlineData(true, "rejected", 400)]
    [InlineData(true, "valid", 200)]
    [InlineData(false, null, 200)]
    public async Task SendCode_DeclaredBotProtection_GatesEmailDelivery(bool enabled, string? token, int statusCode)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var jobs = Substitute.For<IBackgroundJobClient>();
        var handler = new SendCodeCommandHandler(cache, jobs);
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle((SendCodeCommand)call[0], CancellationToken.None));
        var verifier = Substitute.For<ITurnstileVerificationService>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new TurnstileVerificationResult((string)call[0] == "valid", []));
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new BotProtectionSettings { Enabled = enabled }));
        services.AddSingleton(verifier);
        services.AddSingleton<ILogger<RequireBotProtectionFilter>>(NullLogger<RequireBotProtectionFilter>.Instance);
        using var provider = services.BuildServiceProvider();
        var method = typeof(OAuthController).GetMethod(nameof(OAuthController.SendCode))!;
        var route = method.GetCustomAttribute<HttpPostAttribute>()!.Template;
        route.Should().Be("/oauth/send-code");
        var request = JsonSerializer.Deserialize<OAuthController.SendCodeRequest>(
            JsonSerializer.Serialize(new { email = "oauth-person@example.com", language = "pt-BR", turnstileToken = token }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        _controller.HttpContext.Request.Method = "POST";
        _controller.HttpContext.Request.Path = route;
        var context = new ActionExecutingContext(
            new ActionContext(_controller.HttpContext, new RouteData(), new ControllerActionDescriptor { MethodInfo = method }),
            [], new Dictionary<string, object?> { ["request"] = request }, _controller);
        IActionResult? actionResult = null;
        async Task<ActionExecutedContext> InvokeAction()
        {
            actionResult = await _controller.SendCode(request, CancellationToken.None);
            return new ActionExecutedContext(context, context.Filters, _controller) { Result = actionResult };
        }

        var attribute = method.GetCustomAttribute<RequireBotProtectionAttribute>();
        if (attribute is null)
            await InvokeAction();
        else
            await ((IAsyncActionFilter)attribute.CreateInstance(provider)).OnActionExecutionAsync(context, InvokeAction);

        var result = (context.Result ?? actionResult).Should().BeAssignableTo<ObjectResult>().Subject;
        result.StatusCode.Should().Be(statusCode);
        var delivered = statusCode == 200;
        cache.TryGetValue("verify:oauth-person@example.com", out _).Should().Be(delivered);
        jobs.Received(delivered ? 1 : 0).Create(Arg.Any<Job>(), Arg.Any<IState>());
        if (delivered)
            await _mediator.Received(1).Send(Arg.Is<SendCodeCommand>(command => command.Language == "pt-BR"), Arg.Any<CancellationToken>());
        else
            await _mediator.DidNotReceive().Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>());
        await verifier.Received(enabled && token is not null ? 1 : 0)
            .VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendCode_Failure_ReturnsBadRequestWithErrorCode()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.TooManyRequests));

        var request = new OAuthController.SendCodeRequest("test@example.com");
        var result = await _controller.SendCode(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            Error = ErrorMessages.TooManyRequests.Message,
            ErrorCode = ErrorMessages.TooManyRequests.Code
        });
    }

    [Fact]
    public async Task SendCode_NullLanguage_DefaultsToEn()
    {
        _mediator.Send(Arg.Any<SendCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new OAuthController.SendCodeRequest("test@example.com", null);
        var result = await _controller.SendCode(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<SendCodeCommand>(c => c.Language == "en"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyCode_Success_ReturnsOkWithRedirectUrl()
    {
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("redirectUrl");
        json.Should().Contain("/oauth/consent?ticket=");
        ExtractRedirectUrl(result).Should().Contain("claude.ai/callback").And.Contain("state=state-abc");
    }

    [Fact]
    public async Task VerifyCode_Failure_ReturnsBadRequestWithErrorCode()
    {
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>(ErrorMessages.InvalidVerificationCode));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "000000", "state-abc",
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            Error = ErrorMessages.InvalidVerificationCode.Message,
            ErrorCode = ErrorMessages.InvalidVerificationCode.Code
        });
    }

    [Fact]
    public async Task VerifyCode_InvalidRedirectUri_ReturnsBadRequest()
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://evil.com/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task VerifyCode_RedirectUriWithQueryParam_UseAmpersandSeparator()
    {
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://claude.ai/callback?existing=1", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        ExtractRedirectUrl(result).Should().Contain("callback?existing=1&code=");
    }

    [Fact]
    public async Task Token_UnsupportedGrantType_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "client_credentials", "code-abc", "verifier-xyz",
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("unsupported_grant_type");
    }

    [Fact]
    public async Task Token_InvalidCode_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "nonexistent-code", "verifier-xyz",
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_grant");
    }

    [Fact]
    public async Task Token_InvalidRedirectUri_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "nonexistent-code", "verifier-xyz",
            "https://evil.com/callback", CancellationToken.None, client_id: "client-123");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Token_ValidCodeExchange_ReturnsAccessToken()
    {
        var codeVerifier = "test-verifier-that-is-long-enough-for-pkce-validation";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var authCode = _authStore.CreateCode(
            UserId, codeChallenge, "https://claude.ai/callback", "client-123");

        var result = await _controller.Token(
            "authorization_code", authCode, codeVerifier,
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("access_token");
        json.Should().Contain("Bearer");
        await _apiKeyRepo.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Token_MissingClientId_RejectsExchange()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-registered-at-authorize");

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Token_WrongCodeVerifier_ReturnsInvalidGrant()
    {
        var (_, challenge) = GeneratePkce();
        var (otherVerifier, _) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-123");

        var result = await _controller.Token(
            "authorization_code", code, otherVerifier,
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_grant");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Token_ValidExchange_CreatesReadWriteClaudeKeyWithDefaultScopes()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-123");

        ApiKey? created = null;
        _apiKeyRepo.When(r => r.AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => created = callInfo.Arg<ApiKey>());

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        result.Should().BeOfType<OkObjectResult>();
        created.Should().NotBeNull();
        created!.UserId.Should().Be(UserId);
        created.Name.Should().Be("Claude.ai");
        created.IsReadOnly.Should().BeFalse();
        created.IsRevoked.Should().BeFalse();
        created.Scopes.Should().BeEquivalentTo(AgentScopes.ClaudeDefaultScopes);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        var ok = (OkObjectResult)result;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        doc.RootElement.GetProperty("scope").GetString()!.Split(' ')
            .Should().BeEquivalentTo(AgentScopes.ClaudeDefaultScopes);
        BCrypt.Net.BCrypt.Verify(doc.RootElement.GetProperty("access_token").GetString()!, created.KeyHash).Should().BeTrue();
    }

    [Fact]
    public async Task Token_ValidExchange_RevokesPriorClaudeKeysBeforeIssuingNew()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(UserId, challenge, "https://claude.ai/callback", "client-123");

        var priorKey = LegacyConnectorCredentialMigrationTests.CreateLegacyCredential(UserId, scopes: AgentScopes.ClaudeDefaultScopes).Entity;
        _apiKeyRepo.FindTrackedAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<ApiKey, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { priorKey });

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        result.Should().BeOfType<OkObjectResult>();
        priorKey.IsRevoked.Should().BeTrue();
        await _apiKeyRepo.Received(1).AddAsync(
            Arg.Is<ApiKey>(k => k.Name == "Claude.ai"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Register_WithAttackerRedirectUri_ReturnsBadRequest()
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"client_name":"Malicious MCP","redirect_uris":["https://attacker.com/callback"]}""");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("invalid_redirect_uri");
        json.Should().Contain("not in the allowlist");
    }

    [Fact]
    public void Register_WithMixedValidAndInvalidRedirectUris_ReturnsBadRequest()
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"client_name":"Test","redirect_uris":["https://claude.ai/callback","https://attacker.com/callback"]}""");

        var result = _controller.Register(body);

        var obj = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(obj.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Authorize_WithAttackerDomain_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://attacker.com/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task VerifyCode_WithAttackerDomainRedirectUri_ReturnsBadRequest()
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", "https://attacker.com/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Token_WithAttackerDomainRedirectUri_ReturnsBadRequest()
    {
        var result = await _controller.Token(
            "authorization_code", "any-code", "verifier-xyz",
            "https://attacker.com/callback", CancellationToken.None, client_id: "client-123");

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Authorize_WithInvalidSchemeRedirectUri_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "javascript:alert('xss')",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Authorize_WithHttpSchemeRedirectUri_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "http://claude.ai/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Authorize_WithFtpSchemeRedirectUri_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "ftp://claude.ai/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Authorize_WithSubdomainAttempt_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://attacker.claude.ai/callback",
            ResponseType = "code",
            State = "state-abc",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Theory]
    [InlineData("https://attacker.com/callback")]
    [InlineData("https://evil.org/callback")]
    [InlineData("https://malicious.net/callback")]
    public async Task VerifyCode_RejectsUnallowlistedHosts(string redirectUri)
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc",
            "challenge-xyz", redirectUri, "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = JsonSerializer.Serialize(bad.Value);
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Authorize_MissingState_ReturnsBadRequest()
    {
        var result = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "",
            CodeChallenge = "challenge-xyz",
            CodeChallengeMethod = "S256",
        }));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_request");
    }

    [Fact]
    public async Task VerifyCode_MissingState_ReturnsBadRequestAndDoesNotVerifyCode()
    {
        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "",
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var result = await _controller.VerifyCode(request, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("invalid_request");
        await _mediator.DidNotReceive().Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyCode_EchoesStateVerbatim_SoClientCanDetectMismatch()
    {
        var state = "state-" + Guid.NewGuid().ToString("N");
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", state,
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var redirectUrl = ExtractRedirectUrl(await _controller.VerifyCode(request, CancellationToken.None));

        ExtractQueryParam(redirectUrl, "state").Should().Be(state);
        redirectUrl.Should().NotContain("tampered-state");
    }

    [Fact]
    public async Task VerifyCode_StateWithQueryInjection_IsPercentEncodedSoNoParamSmuggling()
    {
        var tamperedState = "benign&code=forged-code&x=";
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", tamperedState,
            "challenge-xyz", "https://claude.ai/callback", "client-123");

        var redirectUrl = ExtractRedirectUrl(await _controller.VerifyCode(request, CancellationToken.None));

        redirectUrl.Should().Contain("state=benign%26code%3Dforged-code");
        redirectUrl.Should().NotContain("&code=forged-code");
        ExtractQueryParam(redirectUrl, "code").Should().NotBe("forged-code");
        ExtractQueryParam(redirectUrl, "state").Should().Be(tamperedState);
    }

    [Fact]
    public async Task Token_WithNonce_EchoesNonceInResponse()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(
            UserId, challenge, "https://claude.ai/callback", "client-123", "replay-nonce");

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("nonce");
        json.Should().Contain("replay-nonce");
    }

    [Fact]
    public async Task Token_WithoutNonce_OmitsNonceFromResponse()
    {
        var (verifier, challenge) = GeneratePkce();
        var code = _authStore.CreateCode(
            UserId, challenge, "https://claude.ai/callback", "client-123");

        var result = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        JsonSerializer.Serialize(ok.Value).Should().NotContain("nonce");
    }

    [Fact]
    public async Task VerifyCode_WithNonce_BindsNonceRetrievableAtTokenExchange()
    {
        var (verifier, challenge) = GeneratePkce();
        var loginResponse = new LoginResponse(UserId, "jwt-token", "Alex", "test@example.com");
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(loginResponse));

        var request = new OAuthController.VerifyCodeRequest(
            "test@example.com", "123456", "state-abc", challenge,
            "https://claude.ai/callback", "client-123", Nonce: "verify-nonce-7");
        var code = ExtractQueryParam(
            ExtractRedirectUrl(await _controller.VerifyCode(request, CancellationToken.None)), "code");

        var tokenResult = await _controller.Token(
            "authorization_code", code, verifier,
            "https://claude.ai/callback", CancellationToken.None, client_id: "client-123");

        var ok = tokenResult.Should().BeOfType<OkObjectResult>().Subject;
        JsonSerializer.Serialize(ok.Value).Should().Contain("verify-nonce-7");
    }

    [Fact]
    public async Task Authorize_PageViewAllocatesNoPendingGoogleRequest()
    {
        var page = (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = "client-123",
            RedirectUri = "https://claude.ai/callback",
            ResponseType = "code",
            State = "client-state",
            CodeChallenge = "mcp-challenge",
            CodeChallengeMethod = "S256",
            Nonce = "mcp-nonce",
        }))
            .Should().BeOfType<ContentResult>().Subject;

        var link = System.Text.RegularExpressions.Regex.Match(page.Content!,
            "/oauth/google/start\\?([^\"]+)").Groups[1].Value;
        link.Should().NotBeEmpty();

        foreach (var pair in System.Net.WebUtility.HtmlDecode(link).Split('&'))
        {
            var value = Uri.UnescapeDataString(pair.Split('=', 2)[1]);
            _authStore.ConsumeGoogleRequest(value).Should().BeNull();
        }
    }

    [Theory]
    [InlineData("/oauth/authorize")]
    [InlineData("/oauth/google/start")]
    public void OAuthBrowserRoutes_CarryTheAuthRateLimit(string template)
    {
        var action = typeof(OAuthController).GetMethods()
            .SingleOrDefault(method => method.GetCustomAttributes(typeof(HttpGetAttribute), false)
                .Cast<HttpGetAttribute>().Any(attribute => attribute.Template == template));

        action.Should().NotBeNull();
        var limit = CustomAttributeData.GetCustomAttributes(action!)
            .Should().ContainSingle(data => data.AttributeType == typeof(DistributedRateLimitAttribute)).Subject;
        limit.ConstructorArguments[0].Value.Should().Be("auth");
    }

    private static string? ReadRenderedError(string page)
    {
        var block = System.Text.RegularExpressions.Regex.Match(page,
            "<script type=\"application/json\" id=\"oauth-request\">(.*?)</script>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        return JsonDocument.Parse(block).RootElement.GetProperty("error").GetString();
    }

    private async Task<IActionResult> StartGoogle(string challenge = "mcp-challenge",
        string clientState = "client-state", string? nonce = null) =>
        (await _controller.GoogleStart("client-123", "https://claude.ai/callback", clientState, challenge, "S256", nonce));

    private GoogleAuthorizationRequest CreatePendingGoogleRequest(string challenge = "mcp-challenge",
        string clientState = "client-state", string? nonce = null) =>
        _authStore.TryCreateGoogleRequest("client-123", "https://claude.ai/callback", clientState,
            challenge, nonce, "https://api.useorbit.org/oauth/google/callback", "en")!;

    private static string Challenge(string verifier) => Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static (string verifier, string challenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return (verifier, challenge);
    }

    private string ExtractRedirectUrl(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return CompleteConsent(doc.RootElement.GetProperty("redirectUrl").GetString()!);
    }

    private string CompleteConsent(string url) => url.StartsWith("/oauth/consent", StringComparison.Ordinal)
        ? ((RedirectResult)_controller.GrantConsent(ExtractQueryParam(url, "ticket"), "requested")).Url
        : url;

    private static string ExtractQueryParam(string url, string key)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == key)
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return string.Empty;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

}
