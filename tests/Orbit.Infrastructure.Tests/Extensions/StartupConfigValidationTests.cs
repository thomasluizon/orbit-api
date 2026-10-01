using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Extensions;

/// <summary>
/// Startup fail-fast guards for required configuration. A missing or empty
/// section/key must throw a specific <see cref="InvalidOperationException"/> at
/// boot rather than surfacing as a null-reference deep in a request path.
/// </summary>
public class StartupConfigValidationTests
{
    [Fact]
    public void AddOrbitAuthentication_MissingJwtSection_ThrowsMissing()
    {
        var builder = BuildWith(new Dictionary<string, string?>());

        var act = () => builder.AddOrbitAuthentication();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Configuration section 'Jwt' is missing.");
    }

    [Theory]
    [InlineData("Jwt:SecretKey")]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    public void AddOrbitAuthentication_IncompleteJwtSection_ThrowsIncomplete(string omittedKey)
    {
        var values = ValidJwt();
        values.Remove(omittedKey);
        var builder = BuildWith(values);

        var act = () => builder.AddOrbitAuthentication();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Configuration section 'Jwt' is incomplete*");
    }

    [Fact]
    public void AddOrbitAuthentication_ValidJwtSection_DoesNotThrow()
    {
        var builder = BuildWith(ValidJwt());

        var act = () => builder.AddOrbitAuthentication();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Supabase:Url")]
    [InlineData("Supabase:AnonKey")]
    [InlineData("Supabase:SecretKey")]
    public void AddOrbitInfrastructure_MissingSupabaseKey_ThrowsWithKeyName(string omittedKey)
    {
        var values = ValidInfrastructure();
        values.Remove(omittedKey);
        var builder = BuildWith(values);

        var act = () => builder.AddOrbitInfrastructure();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{omittedKey}' is missing or empty.");
    }

    [Theory]
    [InlineData("Supabase:Url")]
    [InlineData("Supabase:AnonKey")]
    [InlineData("Supabase:SecretKey")]
    public void AddOrbitInfrastructure_WhitespaceSupabaseKey_ThrowsWithKeyName(string blankedKey)
    {
        var values = ValidInfrastructure();
        values[blankedKey] = "   ";
        var builder = BuildWith(values);

        var act = () => builder.AddOrbitInfrastructure();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{blankedKey}' is missing or empty.");
    }

    [Fact]
    public void AddOrbitInfrastructure_S3_RegistersS3StorageWithoutSupabaseStorageSecret()
    {
        var values = ValidInfrastructure();
        values.Remove("Supabase:SecretKey");
        values["Storage:Provider"] = "S3";
        values["Storage:S3:Bucket"] = "orbit-uploads-staging-713285551626";
        values["Storage:S3:Region"] = "us-east-2";
        values["Storage:S3:PublicBaseUrl"] = "https://api-staging.useorbit.org";
        values["Storage:S3:AccessKeyId"] = "AKIAEXAMPLE123456789";
        values["Storage:S3:SecretAccessKey"] = "example-secret-key";
        values["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit;Password=unused";
        var builder = BuildWith(values);

        builder.AddOrbitInfrastructure();

        builder.Services.Single(service => service.ServiceType == typeof(IObjectStorageService))
            .ImplementationFactory.Should().NotBeNull();
    }

    [Fact]
    public async Task AddOrbitInfrastructure_SupabaseUploadProvider_StillRegistersS3Reader()
    {
        var values = ValidInfrastructure();
        values["Storage:Provider"] = "Supabase";
        values["Storage:S3:Bucket"] = "orbit-uploads-staging-713285551626";
        values["Storage:S3:Region"] = "us-east-2";
        values["Storage:S3:PublicBaseUrl"] = "https://api-staging.useorbit.org";
        values["Storage:S3:AccessKeyId"] = "AKIAEXAMPLE123456789";
        values["Storage:S3:SecretAccessKey"] = "example-secret-key";
        values["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit;Password=unused";
        var s3Values = new Dictionary<string, string?>(values)
        {
            ["Storage:Provider"] = "S3"
        };
        var s3Builder = BuildWith(s3Values);
        s3Builder.AddOrbitInfrastructure();
        using var s3Provider = s3Builder.Services.BuildServiceProvider();
        using var s3Scope = s3Provider.CreateScope();
        const string objectKey = "2b3b1fc7-813c-4eb6-ad02-bd7754299703/1e21a217-e146-43c9-b61c-b5871a991578.webp";
        var signedUpload = await s3Scope.ServiceProvider.GetRequiredService<IObjectStorageService>()
            .CreateSignedUploadAsync(objectKey, "image/webp", 1024);
        signedUpload.PublicUrl.Should().Be($"https://api-staging.useorbit.org/api/uploads/object/{objectKey}");

        var builder = BuildWith(values);

        builder.AddOrbitInfrastructure();

        builder.Services.Single(service => service.ServiceType == typeof(IObjectStorageService))
            .ImplementationType.Should().Be<SupabaseObjectStorageService>();
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IObjectStorageReadService>();
        reader.Should().BeOfType<S3ObjectStorageService>();
        var readUrl = await reader.CreateReadUrlAsync(objectKey);
        new Uri(readUrl).Host.Should().Contain("orbit-uploads-staging-713285551626");
    }

    private static WebApplicationBuilder BuildWith(Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static Dictionary<string, string?> ValidJwt() => new()
    {
        ["Jwt:SecretKey"] = "0123456789abcdef0123456789abcdef",
        ["Jwt:Issuer"] = "OrbitApi",
        ["Jwt:Audience"] = "OrbitClient",
    };

    private static Dictionary<string, string?> ValidInfrastructure() => new()
    {
        ["Supabase:Url"] = "https://example.supabase.co",
        ["Supabase:AnonKey"] = "anon-key",
        ["Supabase:SecretKey"] = "secret-key",
        ["Ses:AccessKeyId"] = "key",
        ["Ses:SecretAccessKey"] = "secret",
        ["Ses:Region"] = "us-east-2",
        ["Ses:FromEmail"] = "Orbit <noreply@send.useorbit.org>",
        ["Ses:SupportEmail"] = "contact@useorbit.org",
        ["Ses:MarketingFromEmail"] = "Orbit <news@updates.useorbit.org>",
        ["Ses:TransactionalConfigurationSet"] = "orbit-transactional",
        ["Ses:MarketingConfigurationSet"] = "orbit-marketing",
        ["Ses:TopicArn"] = "arn:aws:sns:us-east-2:713285551626:orbit-ses-events",
    };
}
