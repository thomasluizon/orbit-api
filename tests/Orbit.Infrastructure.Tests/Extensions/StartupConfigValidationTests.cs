using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Orbit.Api.Extensions;

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
        var values = ValidSupabase();
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
        var values = ValidSupabase();
        values[blankedKey] = "   ";
        var builder = BuildWith(values);

        var act = () => builder.AddOrbitInfrastructure();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{blankedKey}' is missing or empty.");
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

    private static Dictionary<string, string?> ValidSupabase() => new()
    {
        ["Supabase:Url"] = "https://example.supabase.co",
        ["Supabase:AnonKey"] = "anon-key",
        ["Supabase:SecretKey"] = "secret-key",
    };
}
