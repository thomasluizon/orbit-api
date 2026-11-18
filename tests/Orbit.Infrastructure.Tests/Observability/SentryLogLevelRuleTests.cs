using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Extensions;
using Sentry;
using Sentry.AspNetCore;

namespace Orbit.Infrastructure.Tests.Observability;

/// <summary>
/// Holds the effective minimum level that the <c>appsettings.json</c> logging rules give the Sentry
/// breadcrumb and event logger provider, through the real configuration and the real <c>UseSentry</c> registration.
/// </summary>
[Collection("Sentry SDK")]
public class SentryLogLevelRuleTests
{
    private const string UnlistedCategory = "Orbit.Api.Probe.UnlistedCategory";

    [Theory]
    [InlineData("Orbit.Api.Middleware.UnhandledExceptionHandler")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Microsoft.EntityFrameworkCore.Update")]
    public void CategoryRuleAtNone_KeepsItsLogsOutOfSentryBreadcrumbsAndEvents(string category)
    {
        using var capture = SentryCapture.Start();

        var logger = capture.LoggerFactory.CreateLogger(category);
        logger.LogInformation("Silenced probe information");
        logger.LogError(new InvalidOperationException("probe"), "Silenced probe error");

        capture.Breadcrumbs.Should().BeEmpty();
        capture.Events.Should().BeEmpty();
    }

    [Fact]
    public void UnlistedCategory_ReachesSentryAsBreadcrumbAndErrorEvent()
    {
        using var capture = SentryCapture.Start();

        var logger = capture.LoggerFactory.CreateLogger(UnlistedCategory);
        logger.LogInformation("Unlisted probe information");
        logger.LogError("Unlisted probe error");

        capture.Breadcrumbs.Select(breadcrumb => breadcrumb.Message)
            .Should().Contain("Unlisted probe information");
        capture.Events.Should().ContainSingle()
            .Which.Message!.Formatted.Should().Be("Unlisted probe error");
    }

    private sealed class SentryCapture : IDisposable
    {
        private readonly WebApplication _app;

        private SentryCapture(WebApplication app, List<Breadcrumb> breadcrumbs, List<SentryEvent> events)
        {
            _app = app;
            Breadcrumbs = breadcrumbs;
            Events = events;
            LoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        }

        public List<Breadcrumb> Breadcrumbs { get; }

        public List<SentryEvent> Events { get; }

        public ILoggerFactory LoggerFactory { get; }

        public static SentryCapture Start()
        {
            var breadcrumbs = new List<Breadcrumb>();
            var events = new List<SentryEvent>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = "Production",
            });
            builder.AddOrbitObservability();
            builder.Services.PostConfigure<SentryAspNetCoreOptions>(options =>
            {
                options.Dsn = "https://key@localhost/1";
                options.Transport = Substitute.For<Sentry.Extensibility.ITransport>();
                options.SetBeforeBreadcrumb(breadcrumb =>
                {
                    breadcrumbs.Add(breadcrumb);
                    return null;
                });
                options.SetBeforeSend(sentryEvent =>
                {
                    events.Add(sentryEvent);
                    return null;
                });
            });

            return new SentryCapture(builder.Build(), breadcrumbs, events);
        }

        public void Dispose() => ((IDisposable)_app).Dispose();
    }
}
