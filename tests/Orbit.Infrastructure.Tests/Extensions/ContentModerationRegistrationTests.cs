using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests.Extensions;

public class ContentModerationRegistrationTests
{
    [Fact]
    public void AddOrbitAiServices_ResolvesContentModeration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddOrbitAiServices();
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IContentModerationService>()
            .Should().BeOfType<ContentModerationService>();
    }
}
