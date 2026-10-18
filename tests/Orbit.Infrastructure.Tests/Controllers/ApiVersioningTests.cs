using System.Security.Claims;
using System.Text.Json;
using Asp.Versioning;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class ApiVersioningTests : IAsyncLifetime
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly WebApplication _application;
    private readonly RequestDelegate _pipeline;
    private IReadOnlyList<RouteEndpoint> _endpoints = [];

    public ApiVersioningTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Cors:AllowedOrigins:0"] = "https://web.example.com";
        builder.Configuration["Cors:ThirdPartyOrigins:0"] = "https://agent.example.com";
        builder.Configuration["Cors:LandingOrigins:0"] = "https://landing.example.com";
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddApiPipeline(builder);
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddCorsPolicies(builder);
        builder.Services.AddControllers().AddApplicationPart(typeof(HabitsController).Assembly);
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(_mediator);
        builder.Services.AddSingleton(Substitute.For<IUserDateService>());
        builder.Services.AddSingleton(Substitute.For<IGenericRepository<User>>());

        var config = Substitute.For<IAppConfigService>();
        config.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["HabitsLimit"] = "10" });
        config.GetAsync(AppConfigKeys.MinSupportedVersion, "0.0.0", Arg.Any<CancellationToken>())
            .Returns("1.3.57");
        builder.Services.AddSingleton(config);

        var flags = Substitute.For<IFeatureFlagService>();
        flags.ResolveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ResolvedFeatureFlag("redesign", false, null) });
        builder.Services.AddSingleton(flags);

        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PaginatedResponse<HabitScheduleItem>([], 1, 20, 0, 0)));
        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ProfileResponse(
                UserId: _userId, Name: "Member", Email: "member@example.com", TimeZone: "America/Sao_Paulo",
                AiMemoryEnabled: true, AiSummaryEnabled: true,
                HasCompletedOnboarding: true, HasCompletedTour: true,
                HasCreatedFirstHabit: true, HasLoggedFirstHabit: true, HasTriedAstra: false,
                HasCompletedOnboardingChecklist: true, Language: "en", Plan: "Free",
                HasProAccess: false, IsTrialActive: false, TrialEndsAt: null, PlanExpiresAt: null,
                AiMessagesUsed: 0, AiMessagesLimit: 10, HasImportedCalendar: false,
                HasSeenImportPrompt: false, HasGoogleConnection: false,
                SubscriptionInterval: null, SubscriptionSource: null, IsLifetimePro: false,
                WeekStartDay: 1, TotalXp: 0, Level: 1, LevelTitle: "Beginner",
                AdRewardsClaimedToday: 0, CurrentStreak: 0, LongestStreak: 0, StreakFreezesAvailable: 0,
                ThemePreference: null, ColorScheme: null, GoogleCalendarAutoSyncEnabled: false,
                GoogleCalendarAutoSyncStatus: default, GoogleCalendarLastSyncedAt: null,
                CanViewGamification: false, Handle: null, SocialOptIn: false)));

        _application = builder.Build();
        var pipeline = new ApplicationBuilder(_application.Services);
        pipeline.UseRouting();
        pipeline.Use(async (context, next) =>
        {
            if (context.Items.ContainsKey("SelectControllerOnly")
                && context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
                return;
            await next(context);
        });
        pipeline.UseAuthorization();
        pipeline.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            _endpoints = endpoints.DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().ToArray();
        });
        _pipeline = pipeline.Build();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _application.DisposeAsync().AsTask();

    [Theory]
    [InlineData("/api/habits", 200, "items")]
    [InlineData("/api/config", 200, "minVersion")]
    [InlineData("/api/profile", 200, "userId")]
    public async Task OmittedVersionAndV1AndV2_PreserveControllerResponses(
        string path, int status, string property)
    {
        var unversioned = await SendAsync("GET", path, null);
        var v1 = await SendAsync("GET", path, "1.0");
        var v2 = await SendAsync("GET", path, "2.0");

        foreach (var response in new[] { unversioned, v1, v2 })
        {
            response.Response.StatusCode.Should().Be(status);
            response.Response.Headers["api-supported-versions"].ToString().Should().Be("1.0, 2.0");
            using var json = JsonDocument.Parse(ReadBody(response));
            json.RootElement.TryGetProperty(property, out _).Should().BeTrue();
        }

        ReadBody(v1).Should().Be(ReadBody(unversioned));
        ReadBody(v2).Should().Be(ReadBody(unversioned));
        unversioned.Features.Get<IApiVersioningFeature>()!.RequestedApiVersion.Should().Be(new ApiVersion(1, 0));
        v2.Features.Get<IApiVersioningFeature>()!.RequestedApiVersion.Should().Be(new ApiVersion(2, 0));
    }

    [Fact]
    public async Task ProfileNotFound_PreservesErrorResponseAcrossVersions()
    {
        _mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProfileResponse>(ErrorMessages.UserNotFound));
        var unversioned = await SendAsync("GET", "/api/profile", null);
        foreach (var version in new[] { "1.0", "2.0" })
        {
            var response = await SendAsync("GET", "/api/profile", version);
            response.Response.StatusCode.Should().Be(404);
            ReadBody(response).Should().Be(ReadBody(unversioned));
        }
    }

    [Fact]
    public async Task EveryControllerRoute_AcceptsV2AndRejectsUnsupportedVersion()
    {
        _endpoints.Should().NotBeEmpty();
        foreach (var endpoint in _endpoints)
        {
            var path = MaterializePath(endpoint.RoutePattern);
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods;
            foreach (var method in methods)
            {
                var accepted = await SendAsync(method, path, "2.0", selectControllerOnly: true);
                accepted.GetEndpoint().Should().BeSameAs(endpoint, $"{method} {path} must support v2");
                accepted.Features.Get<IApiVersioningFeature>()!.RequestedApiVersion.Should().Be(new ApiVersion(2, 0));

                var rejected = await SendAsync(method, path, "9.0", selectControllerOnly: true);
                rejected.Response.StatusCode.Should().Be(400, $"{method} {path} must reject v9");
                using var error = JsonDocument.Parse(ReadBody(rejected));
                error.RootElement.GetProperty("code").GetString().Should().Be("UnsupportedApiVersion");
                error.RootElement.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("1.0, 2.0")]
    public async Task InvalidVersion_ReturnsBadRequest(string version)
    {
        var response = await SendAsync("GET", "/api/config", version);
        response.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task QueryStringVersion_DoesNotOverrideHeaderDefault()
    {
        var response = await SendAsync("GET", "/api/config", null, query: "?api-version=9.0");
        response.Response.StatusCode.Should().Be(200);
        response.Features.Get<IApiVersioningFeature>()!.RequestedApiVersion.Should().Be(new ApiVersion(1, 0));
    }

    [Theory]
    [InlineData(null, "https://web.example.com", "GET")]
    [InlineData("ThirdParty", "https://agent.example.com", "GET")]
    [InlineData("Landing", "https://landing.example.com", "POST")]
    public async Task CorsPolicies_AllowVersionHeaderAndExposeSupportedVersions(
        string? policyName, string origin, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "OPTIONS";
        context.Request.Headers.Origin = origin;
        context.Request.Headers.AccessControlRequestMethod = method;
        context.Request.Headers.AccessControlRequestHeaders = "api-version, content-type";
        var provider = _application.Services.GetRequiredService<ICorsPolicyProvider>();
        var policy = await provider.GetPolicyAsync(context, policyName);
        var cors = _application.Services.GetRequiredService<ICorsService>();

        var result = cors.EvaluatePolicy(context, policy!);

        result.IsOriginAllowed.Should().BeTrue();
        result.AllowedMethods.Should().Contain(method);
        result.AllowedHeaders.Should().Contain("api-version");
        result.AllowedExposedHeaders.Should().Contain("api-supported-versions");
    }

    [Theory]
    [InlineData("v1", "1.0", "openapi.json", false)]
    [InlineData("v2", "2.0", "openapi_v2.json", true)]
    public void OpenApiDocuments_ContainOnlyTheirVersionAndPreserveSchemas(
        string group, string version, string file, bool requiresHeader)
    {
        var provider = _application.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        var options = _application.Services.GetRequiredService<IOptionsMonitor<OpenApiOptions>>().Get(group);
        var descriptions = provider.ApiDescriptionGroups.Items.SelectMany(item => item.Items).ToArray();
        descriptions.Should().NotBeEmpty();
        foreach (var description in descriptions)
            options.ShouldInclude(description).Should().Be(description.GroupName == group);

        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api"));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file)));
        using var v1 = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "openapi.json")));
        var root = document.RootElement;
        root.GetProperty("info").GetProperty("version").GetString().Should().Be(version);
        root.GetProperty("components").GetRawText().Should().Be(v1.RootElement.GetProperty("components").GetRawText());
        root.GetProperty("paths").EnumerateObject().Select(path => path.Name)
            .Should().BeEquivalentTo(v1.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name));
        foreach (var operation in root.GetProperty("paths").EnumerateObject()
                     .SelectMany(path => path.Value.EnumerateObject()))
        {
            var header = operation.Value.GetProperty("parameters").EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "api-version");
            header.GetProperty("in").GetString().Should().Be("header");
            (header.TryGetProperty("required", out var required) && required.GetBoolean())
                .Should().Be(requiresHeader);
        }
    }

    private async Task<DefaultHttpContext> SendAsync(
        string method, string path, string? version, bool selectControllerOnly = false, string? query = null)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var response = new CallbackResponseFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(response);
        context.RequestServices = scope.ServiceProvider;
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "UnitTest"));
        if (version is not null)
            context.Request.Headers["api-version"] = version;
        if (selectControllerOnly)
            context.Items["SelectControllerOnly"] = true;

        await _pipeline(context);
        await response.StartAsync();
        return context;
    }

    private static string ReadBody(HttpContext context) =>
        System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static string MaterializePath(RoutePattern pattern) => "/" + string.Join("/",
        pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => parameter.ParameterPolicies.Any(policy => policy.Content == "guid")
                ? "ab8ba7d9-0485-4d3b-b01d-4578f84c2c73"
                : parameter.ParameterPolicies.Any(policy => policy.Content == "int") ? "1" : "sample",
            _ => throw new InvalidOperationException($"Unexpected route part {part}.")
        }))));

    private sealed class CallbackResponseFeature : HttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _callbacks = new();

        public override void OnStarting(Func<object, Task> callback, object state) =>
            _callbacks.Push((callback, state));

        public async Task StartAsync()
        {
            while (_callbacks.TryPop(out var entry))
                await entry.Callback(entry.State);
        }
    }
}
