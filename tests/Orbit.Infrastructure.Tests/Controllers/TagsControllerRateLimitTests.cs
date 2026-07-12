using System.Reflection;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public class TagsControllerRateLimitTests
{
    [Theory]
    [InlineData(nameof(TagsController.CreateTag))]
    [InlineData(nameof(TagsController.UpdateTag))]
    [InlineData(nameof(TagsController.DeleteTag))]
    [InlineData(nameof(TagsController.RestoreTag))]
    [InlineData(nameof(TagsController.AssignTags))]
    [InlineData(nameof(TagsController.SuggestTags))]
    public void MutatingActions_AreRateLimited(string actionName)
    {
        var rateLimitAttributes = GetAction(actionName)
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().HaveCount(1);
    }

    [Fact]
    public void GetTags_IsNotRateLimited()
    {
        var rateLimitAttributes = GetAction(nameof(TagsController.GetTags))
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        rateLimitAttributes.Should().BeEmpty();
    }

    private static MethodInfo GetAction(string actionName)
    {
        var method = typeof(TagsController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"TagsController should expose a public action named '{actionName}'");
        return method!;
    }
}
