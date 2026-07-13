using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Orbit.Api.Mcp.Tools;
using Orbit.Api.Middleware;
using Orbit.Api.OpenApi;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Api.Extensions;

public static partial class ServiceCollectionExtensions
{
    private static string RequireConfigValue(WebApplicationBuilder builder, string key)
    {
        var value = builder.Configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Configuration key '{key}' is missing or empty.");
        return value;
    }

    private static void AddEmailAndSupabaseClients(WebApplicationBuilder builder, TimeSpan httpTimeout)
    {
        var supabaseUrl = RequireConfigValue(builder, "Supabase:Url");
        var supabaseAnonKey = RequireConfigValue(builder, "Supabase:AnonKey");
        var supabaseSecretKey = RequireConfigValue(builder, "Supabase:SecretKey");

        builder.Services.AddHttpClient("Supabase", client =>
        {
            client.BaseAddress = new Uri(supabaseUrl);
            client.DefaultRequestHeaders.Add("apikey", supabaseAnonKey);
            client.Timeout = httpTimeout;
        });

        builder.Services.Configure<SupabaseStorageSettings>(
            builder.Configuration.GetSection(SupabaseStorageSettings.SectionName));

        builder.Services.AddHttpClient(SupabaseObjectStorageService.HttpClientName, client =>
        {
            // Secret keys use the apikey header only — on Authorization: Bearer the gateway parses them as a JWT and rejects the request: https://supabase.com/docs/guides/getting-started/migrating-to-new-api-keys
            client.BaseAddress = new Uri(supabaseUrl);
            client.DefaultRequestHeaders.Add("apikey", supabaseSecretKey);
            client.Timeout = httpTimeout;
        });

        builder.Services.AddScoped<IObjectStorageService, SupabaseObjectStorageService>();

        builder.Services.Configure<ResendSettings>(
            builder.Configuration.GetSection(ResendSettings.SectionName));

#pragma warning disable S1075 // Resend API base URL is a stable, well-known endpoint
        builder.Services.AddHttpClient("Resend", client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com");
#pragma warning restore S1075
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["Resend:ApiKey"]}");
            client.Timeout = httpTimeout;
        });

        builder.Services.AddScoped<IEmailService, ResendEmailService>();

        builder.Services.Configure<WaitlistSettings>(
            builder.Configuration.GetSection(WaitlistSettings.SectionName));
        builder.Services.Configure<MarketingSettings>(
            builder.Configuration.GetSection(MarketingSettings.SectionName));
        builder.Services.AddSingleton<IWaitlistConfirmationTokenService, WaitlistConfirmationTokenService>();
        builder.Services.AddSingleton<IMarketingUnsubscribeTokenService, MarketingUnsubscribeTokenService>();
        builder.Services.AddScoped<IMarketingContactsService, ResendContactsService>();
    }

    private static void AddStripeBilling(WebApplicationBuilder builder, TimeSpan httpTimeout)
    {
        builder.Services.Configure<StripeSettings>(
            builder.Configuration.GetSection(StripeSettings.SectionName));
        var stripeKey = builder.Configuration.GetSection(StripeSettings.SectionName).Get<StripeSettings>()?.SecretKey;
        Stripe.StripeConfiguration.MaxNetworkRetries = 2;
        if (!string.IsNullOrEmpty(stripeKey))
        {
            Stripe.StripeConfiguration.ApiKey = stripeKey;
            var stripeHttpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                Timeout = httpTimeout,
            };
            Stripe.StripeConfiguration.StripeClient = new Stripe.StripeClient(
                stripeKey,
                httpClient: new Stripe.SystemNetHttpClient(stripeHttpClient, Stripe.StripeConfiguration.MaxNetworkRetries));
        }

        builder.Services.AddSingleton<Stripe.CustomerService>();
        builder.Services.AddSingleton<Stripe.Checkout.SessionService>();
        builder.Services.AddSingleton<Stripe.BillingPortal.SessionService>();
        builder.Services.AddSingleton<Stripe.SubscriptionService>();
        builder.Services.AddSingleton<Stripe.InvoiceService>();
        builder.Services.AddSingleton<Stripe.PriceService>();
        builder.Services.AddSingleton<Stripe.CouponService>();
        builder.Services.AddScoped<Orbit.Application.Common.IBillingService, Orbit.Infrastructure.Services.StripeBillingService>();
        builder.Services.AddScoped<Orbit.Application.Subscriptions.Services.IPriceResolver, Orbit.Application.Subscriptions.Services.PriceResolver>();
    }

    private static void AddGooglePlayBilling(WebApplicationBuilder builder, TimeSpan httpTimeout)
    {
        builder.Services.Configure<GooglePlaySettings>(
            builder.Configuration.GetSection(GooglePlaySettings.SectionName));
        builder.Services.AddSingleton(sp =>
        {
            var googlePlaySettings = sp.GetRequiredService<IOptions<GooglePlaySettings>>().Value;
            var credential = Google.Apis.Auth.OAuth2.CredentialFactory
                .FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(googlePlaySettings.ServiceAccountJson)
                .ToGoogleCredential()
                .CreateScoped(Google.Apis.AndroidPublisher.v3.AndroidPublisherService.Scope.Androidpublisher);
            var service = new Google.Apis.AndroidPublisher.v3.AndroidPublisherService(
                new Google.Apis.Services.BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Orbit",
                });
            service.HttpClient.Timeout = httpTimeout;
            return service;
        });
        builder.Services.AddScoped<Orbit.Application.Common.IPlayBillingService, Orbit.Infrastructure.Services.GooglePlayBillingService>();
        builder.Services.AddSingleton<Orbit.Application.Common.IPlayPushTokenValidator, Orbit.Infrastructure.Services.GooglePlayPushTokenValidator>();
        builder.Services.AddScoped<Orbit.Application.Subscriptions.Services.IPlayReferralCouponConsumer, Orbit.Application.Subscriptions.Services.PlayReferralCouponConsumer>();
    }

    private static void AddPushAndReferralServices(WebApplicationBuilder builder, TimeSpan httpTimeout)
    {
        builder.Services.Configure<VapidSettings>(
            builder.Configuration.GetSection(VapidSettings.SectionName));
        builder.Services.AddHttpClient<IPushNotificationService, PushNotificationService>()
            .ConfigureHttpClient(c => c.Timeout = httpTimeout);
        builder.Services.AddScoped<IReferralRewardService, StripeCouponRewardService>();
        builder.Services.AddScoped<Orbit.Application.Referrals.Commands.ReferralRepositories>(sp =>
            new Orbit.Application.Referrals.Commands.ReferralRepositories(
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.User>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Referral>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Habit>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.HabitLog>>(),
                sp.GetRequiredService<IGenericRepository<Orbit.Domain.Entities.Notification>>()));
    }

    private static void AddOrbitDistributedCache(WebApplicationBuilder builder)
    {
        var redisSettings = builder.Configuration.GetSection(RedisCacheSettings.SectionName).Get<RedisCacheSettings>()
            ?? new RedisCacheSettings();

        if (!redisSettings.Enabled)
        {
            builder.Services.AddDistributedMemoryCache();
            return;
        }

        if (string.IsNullOrWhiteSpace(redisSettings.ConnectionString))
            throw new InvalidOperationException(
                $"{RedisCacheSettings.SectionName}:Enabled is true but {RedisCacheSettings.SectionName}:ConnectionString is not configured.");

        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisSettings.ConnectionString;
            options.InstanceName = redisSettings.InstanceName;
        });
    }

    private static void AddCorsPolicies(WebApplicationBuilder builder)
    {
        var firstPartyOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000"];
        var thirdPartyOrigins = builder.Configuration.GetSection("Cors:ThirdPartyOrigins").Get<string[]>()
            ?? [];
        var landingOrigins = builder.Configuration.GetSection("Cors:LandingOrigins").Get<string[]>()
            ?? [];
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(firstPartyOrigins)
                      .WithHeaders("Authorization", "Content-Type", "Mcp-Session-Id")
                      .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                      .AllowCredentials();
            });
            if (thirdPartyOrigins.Length > 0)
            {
                options.AddPolicy("ThirdParty", policy =>
                {
                    policy.WithOrigins(thirdPartyOrigins)
                          .WithHeaders("Authorization", "Content-Type", "Mcp-Session-Id")
                          .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS");
                });
            }
            if (landingOrigins.Length > 0)
            {
                options.AddPolicy("Landing", policy =>
                {
                    policy.WithOrigins(landingOrigins)
                          .WithHeaders("Content-Type")
                          .WithMethods("POST", "OPTIONS");
                });
            }
        });
    }

    internal static void AddCookieAndKestrelLimits(WebApplicationBuilder builder)
    {
        builder.Services.Configure<CookiePolicyOptions>(options =>
        {
            options.HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always;
            options.Secure = CookieSecurePolicy.Always;
            options.MinimumSameSitePolicy = SameSiteMode.Strict;
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
        });
    }

    private static void AddMcpToolServer(WebApplicationBuilder builder)
    {
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<AgentTools>()
            .WithTools<HabitTools>()
            .WithTools<TagTools>()
            .WithTools<GoalTools>()
            .WithTools<ProfileTools>()
            .WithTools<GamificationTools>()
            .WithTools<NotificationTools>()
            .WithTools<SubscriptionTools>()
            .WithTools<UserFactTools>()
            .WithTools<CalendarTools>()
            .WithTools<AccountTools>()
            .WithTools<ApiKeyTools>()
            .WithTools<SupportTools>()
            .WithTools<ChecklistTemplateTools>()
            .WithTools<FeatureTools>();
    }

    private static void AddApiPipeline(WebApplicationBuilder builder)
    {
        AddResponseCompression(builder);

        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
        builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
        builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = ctx =>
            {
                ctx.ProblemDetails.Extensions.Remove("exception");
                ctx.ProblemDetails.Detail = null;
            };
        });

        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        });
    }

    private static void AddResponseCompression(WebApplicationBuilder builder)
    {
        builder.Services.AddResponseCompression(options =>
        {
            // EnableForHttps is required because Render terminates TLS upstream (X-Forwarded-Proto=https), so compression would otherwise never apply; BREACH is not a concern here as responses are parameterized JSON with no attacker-reflected secrets: https://learn.microsoft.com/aspnet/core/performance/response-compression
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                ["application/json", "application/problem+json"]);
        });

        builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
    }

    private static void InitializeFirebase(ConfigurationManager configuration)
    {
        var firebaseCredJson = configuration["Firebase:CredentialsJson"];
        if (!string.IsNullOrEmpty(firebaseCredJson))
        {
            FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
            {
                Credential = Google.Apis.Auth.OAuth2.CredentialFactory
                    .FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(firebaseCredJson)
                    .ToGoogleCredential()
            });
            return;
        }

        var firebaseCredPath = configuration["Firebase:CredentialsPath"];
        if (!string.IsNullOrEmpty(firebaseCredPath) && File.Exists(firebaseCredPath))
        {
            FirebaseAdmin.FirebaseApp.Create(new FirebaseAdmin.AppOptions
            {
                Credential = Google.Apis.Auth.OAuth2.CredentialFactory
                    .FromFile<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(firebaseCredPath)
                    .ToGoogleCredential()
            });
        }
    }
}
