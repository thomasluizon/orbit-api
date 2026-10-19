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
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(168, 0)]
    [InlineData(0, 61)]
    public void ValidateOrbitSecuritySettings_InvalidJwtLifetime_ThrowsWithSettings(int hours, int minutes)
    {
        var values = ValidJwt();
        values["Jwt:ExpiryHours"] = hours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        values["Jwt:ExpiryMinutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var builder = BuildWith(values);
        builder.Environment.EnvironmentName = "Development";

        var act = () => builder.ValidateOrbitSecuritySettings();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:ExpiryMinutes*Jwt:ExpiryHours*60*");
    }

    [Fact]
    public void ValidateOrbitSecuritySettings_ValidJwtLifetime_DoesNotThrow()
    {
        var builder = BuildWith(ValidJwt());
        builder.Environment.EnvironmentName = "Development";

        var act = () => builder.ValidateOrbitSecuritySettings();

        act.Should().NotThrow();
    }

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

    [Fact]
    public void AddOrbitInfrastructure_WithoutLegacyAuthConfiguration_DoesNotThrow()
    {
        var builder = BuildWith(ValidInfrastructure());

        var act = () => builder.AddOrbitInfrastructure();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Storage:S3:Bucket")]
    [InlineData("Storage:S3:PublicBaseUrl")]
    [InlineData("Storage:S3:Region")]
    [InlineData("Storage:S3:AccessKeyId")]
    [InlineData("Storage:S3:SecretAccessKey")]
    public void AddOrbitInfrastructure_MissingS3Key_ThrowsWithKeyName(string omittedKey)
    {
        var values = ValidInfrastructure();
        values.Remove(omittedKey);
        var act = () => BuildWith(values).AddOrbitInfrastructure();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{omittedKey}' is missing or empty.");
    }

    [Theory]
    [InlineData("Storage:S3:Bucket")]
    [InlineData("Storage:S3:PublicBaseUrl")]
    [InlineData("Storage:S3:Region")]
    [InlineData("Storage:S3:AccessKeyId")]
    [InlineData("Storage:S3:SecretAccessKey")]
    public void AddOrbitInfrastructure_WhitespaceS3Key_ThrowsWithKeyName(string blankedKey)
    {
        var values = ValidInfrastructure();
        values[blankedKey] = "   ";
        var act = () => BuildWith(values).AddOrbitInfrastructure();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{blankedKey}' is missing or empty.");
    }

    [Fact]
    public async Task AddOrbitInfrastructure_RegistersS3UploadsAndReaderWithoutProviderSetting()
    {
        var builder = BuildWith(ValidInfrastructure());
        builder.AddOrbitInfrastructure();
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        const string objectKey = "2b3b1fc7-813c-4eb6-ad02-bd7754299703/1e21a217-e146-43c9-b61c-b5871a991578.webp";
        var writer = scope.ServiceProvider.GetRequiredService<IObjectStorageService>();
        var signedUpload = await writer.CreateSignedUploadAsync(objectKey, "image/webp", 1024);
        signedUpload.PublicUrl.Should().Be($"https://api-staging.useorbit.org/api/uploads/object/{objectKey}");

        var reader = scope.ServiceProvider.GetRequiredService<IObjectStorageReadService>();
        reader.Should().BeOfType<S3ObjectStorageService>().And.BeSameAs(writer);
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
        ["Jwt:ExpiryHours"] = "0",
        ["Jwt:ExpiryMinutes"] = "15",
    };

    private static Dictionary<string, string?> ValidInfrastructure() => new()
    {
        ["Storage:S3:Bucket"] = "orbit-uploads-staging-713285551626",
        ["Storage:S3:Region"] = "us-east-2",
        ["Storage:S3:PublicBaseUrl"] = "https://api-staging.useorbit.org",
        ["Storage:S3:AccessKeyId"] = "AKIAEXAMPLE123456789",
        ["Storage:S3:SecretAccessKey"] = "example-secret-key",
        ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit;Password=unused",
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
