using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Extensions;

public class UploadStorageRegistrationTests
{
    [Theory]
    [InlineData("S3")]
    [InlineData("Supabase")]
    public void AddEmailAndSupabaseClients_RegistersBothDeletionProvidersRegardlessOfUploadProvider(string provider)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = provider,
            ["Storage:S3:Bucket"] = "uploads-test",
            ["Storage:S3:PublicBaseUrl"] = "https://api.example.com",
            ["Storage:S3:Region"] = "us-east-2",
            ["Storage:S3:AccessKeyId"] = "test-access-key",
            ["Storage:S3:SecretAccessKey"] = "test-secret",
            ["Supabase:Url"] = "https://project.supabase.co",
            ["Supabase:AnonKey"] = "test-anon-key",
            ["Supabase:SecretKey"] = "test-service-key",
            ["Ses:AccessKeyId"] = "test-ses-key",
            ["Ses:SecretAccessKey"] = "test-ses-secret",
            ["Ses:Region"] = "us-east-2",
            ["Ses:FromEmail"] = "from@example.com",
            ["Ses:SupportEmail"] = "support@example.com",
            ["Ses:MarketingFromEmail"] = "marketing@example.com",
            ["Ses:TransactionalConfigurationSet"] = "transactional",
            ["Ses:MarketingConfigurationSet"] = "marketing",
            ["Ses:TopicArn"] = "arn:aws:sns:us-east-2:123456789012:test",
        });

        Orbit.Api.Extensions.ServiceCollectionExtensions.AddEmailAndSupabaseClients(builder, TimeSpan.FromSeconds(10));

        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        var stores = scope.ServiceProvider.GetServices<IObjectStorageDeletionService>().ToArray();
        stores.Should().HaveCount(2);
        stores.Should().ContainSingle(store => store is S3ObjectStorageService);
        stores.Should().ContainSingle(store => store is SupabaseObjectStorageService);
        var uploads = scope.ServiceProvider.GetRequiredService<IObjectStorageService>();
        uploads.GetType().Should().Be(provider == "S3" ? typeof(S3ObjectStorageService) : typeof(SupabaseObjectStorageService));
    }
}
