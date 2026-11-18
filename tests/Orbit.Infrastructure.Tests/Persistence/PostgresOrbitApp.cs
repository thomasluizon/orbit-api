using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Domain.Events;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// The composed API services over a disposable PostgreSQL database. The account event bus and
/// the job client are recording doubles, and each request carries a fixed event origin. The model
/// endpoint is an unreachable local address, so no test can reach a paid provider.
/// </summary>
internal sealed class PostgresOrbitApp : IAsyncDisposable
{
    internal const string Origin = "device-origin";

    private readonly ServiceProvider _provider;

    private PostgresOrbitApp(ServiceProvider provider, string connectionString, IAccountEventBus bus, RecordingJobClient jobs)
    {
        _provider = provider;
        ConnectionString = connectionString;
        Bus = bus;
        Jobs = jobs;
    }

    internal string ConnectionString { get; }
    internal IAccountEventBus Bus { get; }
    internal RecordingJobClient Jobs { get; }

    internal static async Task<PostgresOrbitApp> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresTestDatabase.ConnectionVariable))
        {
            Database = $"orbit_effect_test_{Guid.NewGuid():N}",
            Pooling = false
        }.ConnectionString;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection,
            ["AI:ApiKey"] = "unused-test-key",
            ["AI:BaseUrl"] = "http://127.0.0.1:9/v1",
            ["Storage:S3:Bucket"] = "orbit-test",
            ["Storage:S3:Region"] = "us-east-2",
            ["Storage:S3:PublicBaseUrl"] = "https://example.test",
            ["Storage:S3:AccessKeyId"] = "test-key",
            ["Storage:S3:SecretAccessKey"] = "test-secret",
            ["Ses:AccessKeyId"] = "key",
            ["Ses:SecretAccessKey"] = "secret",
            ["Ses:Region"] = "us-east-2",
            ["Ses:FromEmail"] = "Orbit <test@example.test>",
            ["Ses:SupportEmail"] = "test@example.test",
            ["Ses:MarketingFromEmail"] = "Orbit <news@example.test>",
            ["Ses:TransactionalConfigurationSet"] = "orbit-transactional",
            ["Ses:MarketingConfigurationSet"] = "orbit-marketing",
            ["Ses:TopicArn"] = "arn:aws:sns:us-east-2:000000000000:orbit-test"
        });
        builder.AddOrbitDatabase().AddOrbitAiServices().AddOrbitInfrastructure().AddOrbitProductAnalytics();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext.Request.Headers["X-Orbit-Event-Origin"] = Origin;
        builder.Services.Replace(ServiceDescriptor.Singleton<IHttpContextAccessor>(accessor));
        configure?.Invoke(builder.Services);
        var bus = Substitute.For<IAccountEventBus>();
        var jobs = new RecordingJobClient();
        builder.Services.Replace(ServiceDescriptor.Singleton(bus));
        builder.Services.Replace(ServiceDescriptor.Singleton<IBackgroundJobClient>(jobs));
        var provider = builder.Services.BuildServiceProvider();
        var started = new PostgresOrbitApp(provider, connection, bus, jobs);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrbitDbContext>().GetService<IMigrator>().MigrateAsync();
        return started;
    }

    internal AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    internal async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<T> ReadAsync<T>(Func<OrbitDbContext, Task<T>> read)
    {
        await using var scope = CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<OrbitDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrbitDbContext>().Database.EnsureDeletedAsync();
        await _provider.DisposeAsync();
    }

    internal sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];

        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Created.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
