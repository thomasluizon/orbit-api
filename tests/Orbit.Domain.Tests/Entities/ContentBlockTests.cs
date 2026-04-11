using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class ContentBlockTests
{
    [Fact]
    public void Create_ValidInput_SetsAllProperties()
    {
        var block = ContentBlock.Create("welcome_message", "en", "Welcome to Orbit!", "onboarding");

        block.Key.Should().Be("welcome_message");
        block.Locale.Should().Be("en");
        block.Content.Should().Be("Welcome to Orbit!");
        block.Category.Should().Be("onboarding");
        block.UpdatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_DifferentLocale_SetsLocaleCorrectly()
    {
        var block = ContentBlock.Create("welcome_message", "pt-BR", "Bem-vindo ao Orbit!", "onboarding");

        block.Locale.Should().Be("pt-BR");
        block.Content.Should().Be("Bem-vindo ao Orbit!");
    }

    [Fact]
    public void Update_ChangesContentAndTimestamp()
    {
        var block = ContentBlock.Create("key", "en", "Original content", "category");
        var originalTimestamp = block.UpdatedAtUtc;

        block.Update("Updated content");

        block.Content.Should().Be("Updated content");
        block.UpdatedAtUtc.Should().BeOnOrAfter(originalTimestamp);
    }

    [Fact]
    public void Update_PreservesOtherProperties()
    {
        var block = ContentBlock.Create("my_key", "en", "Old", "tips");

        block.Update("New content");

        block.Key.Should().Be("my_key");
        block.Locale.Should().Be("en");
        block.Category.Should().Be("tips");
        block.Content.Should().Be("New content");
    }
}
